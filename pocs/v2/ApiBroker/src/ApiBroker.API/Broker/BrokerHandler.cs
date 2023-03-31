using ApiBroker.API.Configuracoes;
using ApiBroker.API.Identificacao;
using ApiBroker.API.Mapeamento;
using ApiBroker.API.Requisicao;
using ApiBroker.API.Monitoramento;

namespace ApiBroker.API.Broker;

public class BrokerHandler
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;

    public BrokerHandler(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _configuration = configuration;
    }

    public async Task Invoke(HttpContext context)
    {
        var solicitacao = ObterRecursoSolicitado(context.Request.Path);
        if (solicitacao is null)
            return;

        // todo: colocar while para tentar todos provedores até conseguir uma resposta
        
        var provedorAlvo = ObterProvedorAlvo(solicitacao.Provedores);
        if (provedorAlvo is null)
            return;

        var mapeador = new Mapeador();
        var requisicao = mapeador.MapearRequisicao(context, solicitacao, provedorAlvo);

        var requisitor = new Requisitor();
        var (respostaProvedor, tempoRespostaMs) = await requisitor.EnviarRequisicao(requisicao);

        var respostaMapeada =
            mapeador.MapearResposta(respostaProvedor, provedorAlvo, solicitacao.CamposResposta, context);

        LogResultado(solicitacao, provedorAlvo, respostaProvedor, tempoRespostaMs);

        context.Response.StatusCode = (int)respostaMapeada.StatusCode;
        mapeador.CopiarHeadersRespostaProvedor(context, respostaMapeada);
        await respostaMapeada.Content.CopyToAsync(context.Response.Body);
    }

    /// <summary>
    /// Obtém as configurações do recurso solicitado na rota da requisição
    /// </summary>
    /// <param name="rota">Rota da requisição recebida</param>
    /// <returns>Configurações do recurso solicitado</returns>
    private SolicitacaoDto? ObterRecursoSolicitado(PathString rota)
    {
        var identificador = new Identificador();
        return identificador.IdentificarRecursoSolicitado(rota, _configuration);
    }

    /// <summary>
    /// Obtém o provedor mais disponível para atender a requisição do cliente
    /// </summary>
    /// <param name="provedores">Lista de provedores configurada para o recurso</param>
    /// <returns>Configurações do provedor mais disponível</returns>
    private ProvedorSettings? ObterProvedorAlvo(IReadOnlyList<ProvedorSettings> provedores)
    {
        /*
         * todo: pendente de definir e implementar lógica de monitoramento/ranqueamento
         *  Remover o uso do Random() para utilizar o Ranqueador
         */

        if (!provedores.Any())
            return null;

        var random = new Random();
        var provedorAleatorio = random.Next(provedores.Count);
        return provedores[provedorAleatorio];
    }

    private void LogResultado(SolicitacaoDto solicitacao, ProvedorSettings provedorAlvo,
        HttpResponseMessage respostaProvedor, long tempoRespostaMs)
    {
        var monitorador = new Monitorador();
        var logDto = new LogDto
        {
            NomeRecurso = solicitacao.Nome,
            NomeProvedor = provedorAlvo.Nome,
            TempoRespostaMs = tempoRespostaMs,
            Sucesso = respostaProvedor.IsSuccessStatusCode,
            Origem = "RequisicaoCliente"
        };
        monitorador.Log(logDto);
    }
}