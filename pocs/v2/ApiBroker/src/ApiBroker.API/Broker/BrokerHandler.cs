using System.Net;
using ApiBroker.API.Configuracoes;
using ApiBroker.API.Mapeamento;
using ApiBroker.API.Requisicao;
using ApiBroker.API.Dados;
using ApiBroker.API.Ranqueamento;
using ApiBroker.API.Validacao;
using ApiBroker.API.WebSocket;
using Microsoft.AspNetCore.SignalR;

namespace ApiBroker.API.Broker;

public class BrokerHandler
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BrokerHandler> _logger;

    public BrokerHandler(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _configuration = configuration;
        _logger = LoggerFactory.Factory().CreateLogger<BrokerHandler>();
    }

    /// <summary>
    /// Método invocado para manipular a requisição quando
    /// o cliente chama o Broker no endpoint configurado
    /// </summary>
    /// <param name="context">Contexto da requisição</param>
    public async Task Invoke(HttpContext context)
    {
        var path = context.Request.Path;
        
        _logger.LogInformation("Requisição recebida em {Path}", path);
        
        var solicitacao = ObterRecursoSolicitado(path);
        if (solicitacao is null)
        {
            _logger.LogWarning("Erro ao obter todos os detalhes da solicitação. A requisição será encerrada");
            return;
        }

        var mapeador = new Mapeador();
        RespostaMapeada respostaMapeada = new();

        var listaProvedores = await ObterOrdemMelhoresProvedores(solicitacao);
        if (listaProvedores is null || !listaProvedores.Any())
        {
            _logger.LogWarning("Não há provedores disponíveis para atender a requisição");
            // todo: retornar erro quando não houver provedores que atendam aos critérios, por enquanto (nas próximas versões, talvez seja melhor enviar para qualquer um)
            context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            return;
        }
        
        _logger.LogInformation("Iniciando consulta aos provedores na ordem de melhor para o pior");
        foreach (var provedor in listaProvedores)
        {
            var provedorAlvo = ObterDadosProvedorAlvo(solicitacao.NomeRecurso, provedor);
            if (provedorAlvo is null)
            {
                _logger.LogWarning("Configurações incorretas para o provedor com nome: {NomeProvedor}", provedor);
                return;
            }

            _logger.LogInformation("Chamando {NomeProvedor}", provedorAlvo.Nome);

            var requisicao = mapeador.MapearRequisicao(context, solicitacao, provedorAlvo);

            var requisitor = new Requisitor();
            var (respostaProvedor, tempoRespostaMs) = await requisitor.EnviarRequisicao(requisicao, provedorAlvo.Nome, solicitacao.NomeRecurso);
            LogResultado(solicitacao, provedorAlvo, respostaProvedor, tempoRespostaMs);
            if (respostaProvedor is null)
            {
                _logger.LogWarning("Não foi possível obter resposta no provedor {NomeProvedor}", provedor);
                break;
            }

            respostaMapeada = mapeador.MapearResposta(respostaProvedor, provedorAlvo, solicitacao.CamposResposta);

            var validador = new Validador(solicitacao);
            var resultadoValido = validador.Validar(respostaMapeada);
            await NotificarUi(context, listaProvedores.ToArray(), provedorAlvo.Nome);
            if (resultadoValido)
            {
                _logger.LogInformation("O provedor {NomeProvedor} atingiu os critérios da requisição", provedor);
                break;
            }
        }
        
        context.Response.StatusCode = (int)(respostaMapeada?.HttpResponseMessage?.StatusCode ?? HttpStatusCode.ServiceUnavailable);
        if (respostaMapeada != null && respostaMapeada.HttpResponseMessage != null)
        {
            mapeador.CopiarHeadersRespostaProvedor(context, respostaMapeada.HttpResponseMessage);
            await respostaMapeada.HttpResponseMessage.Content.CopyToAsync(context.Response.Body);
        }
    }

    /// <summary>
    /// Obtém as configurações do recurso solicitado na rota da requisição
    /// </summary>
    /// <param name="rota">Rota da requisição recebida</param>
    /// <returns>Configurações do recurso solicitado</returns>
    private SolicitacaoDto ObterRecursoSolicitado(PathString rota)
    {
        var identificador = new Identificador();
        return identificador.IdentificarRecursoSolicitado(rota, _configuration);
    }

    private async Task<List<string>> ObterOrdemMelhoresProvedores(SolicitacaoDto solicitacao)
    {
        var ranqueador = new Ranqueador();
        var todosProvedoresDisponiveis =  await ranqueador.ObterOrdemMelhoresProvedores(solicitacao, _configuration);

        return solicitacao.TentarTodosProvedoresAteSucesso
            ? todosProvedoresDisponiveis
            : todosProvedoresDisponiveis.Take(1).ToList();
    }

    /// <summary>
    /// Obtém o provedor mais disponível para atender a requisição do cliente
    /// </summary>
    /// <param name="nomeRecurso">Nome do recurso solicitado pelo cliente</param>
    /// <param name="nomeProvedor">Nome do provedor alvo</param>
    /// <returns>Configurações do provedor mais disponível</returns>
    private ProvedorSettings ObterDadosProvedorAlvo(string nomeRecurso, string nomeProvedor)
    {
        return ConfiguracoesUtils.ObterDadosProvedorRecurso(nomeRecurso, nomeProvedor, _configuration);
    }

    private void LogResultado(SolicitacaoDto solicitacao, ProvedorSettings provedorAlvo,
        HttpResponseMessage respostaProvedor, long tempoRespostaMs)
    {
        var monitorador = new MetricasDao();
        var logDto = new LogDto
        {
            NomeRecurso = solicitacao.NomeRecurso,
            NomeProvedor = provedorAlvo.Nome,
            TempoRespostaMs = tempoRespostaMs,
            Sucesso = respostaProvedor?.IsSuccessStatusCode ?? false,
            Origem = "RequisicaoCliente"
        };
        monitorador.Log(logDto, _configuration);
    }

    private async Task NotificarUi(HttpContext context, string[] provedoresDisponiveis, string provedorAlvo)
    {
        var ranqueamentoHub = context.RequestServices.GetRequiredService<IHubContext<RanqueamentoHub>>();
        await ranqueamentoHub.Clients.All.SendAsync("ReceiveMessage", provedoresDisponiveis, provedorAlvo);
    }
}