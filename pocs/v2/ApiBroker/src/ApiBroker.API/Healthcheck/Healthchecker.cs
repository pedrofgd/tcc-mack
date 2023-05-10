using ApiBroker.API.Configuracoes;
using ApiBroker.API.Requisicao;
using ApiBroker.API.Dados;

namespace ApiBroker.API.Healthcheck;

public class Healthchecker
{
    private readonly ILogger<Healthchecker> _logger;

    public Healthchecker()
    {
        _logger = LoggerFactory.Factory().CreateLogger<Healthchecker>();
        
    }

    /// <summary>
    /// Faz uma requisições para o Provedor na rota de healthcheck configurada pelo Cliente e
    /// repetidas vezes, enquanto a aplicação estiver sendo executada, no intervalo configurado
    /// </summary>
    /// <param name="nomeRecurso">Nome do recurso para qual o provedor foi configurado</param>
    /// <param name="provedor">Configurações do provedor alvo</param>
    /// <param name="configuration">Configurações</param>
    public async Task CheckPeriodicamente(string nomeRecurso, ProvedorSettings provedor, IConfiguration configuration)
    {
        /*
         * todo: disparando o primeiro manualmente, e então agendando os próximos
         *  Confirmar se há outra forma
         */
        await Check(nomeRecurso, provedor, configuration);
        
        // Vai executar repetidamente no intervalo configurado no timer
        var intervalo = TimeSpan.FromSeconds(provedor.Healthcheck!.IntervaloEmSegundos);
        var timer = new PeriodicTimer(intervalo);
        while (await timer.WaitForNextTickAsync())
        {
            await Check(nomeRecurso, provedor, configuration);
        }
    }

    private async Task Check(string nomeRecurso, ProvedorSettings provedor, IConfiguration configuration)
    {
        var requisitor = new Requisitor();

        var requisicao = new HttpRequestMessage(HttpMethod.Get, provedor.Healthcheck!.RotaHealthcheck);
        var (resposta, tempoRespostaMs) = await requisitor.EnviarRequisicao(requisicao, provedor.Nome, nomeRecurso);

        LogResultado(nomeRecurso, provedor, resposta, tempoRespostaMs, configuration);

        var valido = resposta.IsSuccessStatusCode;
        var msg = $"Healthcheck {nomeRecurso}/{provedor.Nome} válido: {valido}";
        _logger.LogDebug(msg);
    }

    private void LogResultado(string nomeRecurso, ProvedorSettings provedor,
        HttpResponseMessage resultadoCheck, long tempoRespostaMs, IConfiguration configuration)
    {
        var monitorador = new MetricasDao();
        var logDto = new LogDto
        {
            NomeRecurso = nomeRecurso,
            NomeProvedor = provedor.Nome,
            TempoRespostaMs = tempoRespostaMs,
            Sucesso = resultadoCheck.IsSuccessStatusCode,
            Origem = "Healthcheck"
        };
        monitorador.Log(logDto, configuration);
    }
}