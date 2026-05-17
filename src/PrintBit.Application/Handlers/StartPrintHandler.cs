using Microsoft.Extensions.Logging;
using PrintBit.Application.Events;
using PrintBit.Application.StateMachine;
using PrintBit.Infrastructure.Services.PrintService;

namespace PrintBit.Application.Handlers;

public class StartPrintHandler
{
    private readonly ILogger<StartPrintHandler> _logger;

    private readonly TransactionStateMachine _stateMachine;

    private readonly IPrintService _printService;

    public StartPrintHandler(
        ILogger<StartPrintHandler> logger,
        TransactionStateMachine stateMachine,
        IPrintService printService)
    {
        _logger = logger;

        _stateMachine = stateMachine;

        _printService = printService;
    }

    public async Task HandleAsync(
        StartPrintEvent evt,
        CancellationToken cancellationToken = default)
    {
        _stateMachine.StartPrinting();

        var result = await _printService.PrintAsync(
            new PrintJobRequest
            {
                FilePath = evt.FilePath,

                PrinterName = "EPSON L5290 Series"
            },
            cancellationToken);

        if (!result.Success)
        {
            _logger.LogError(
                "Print failed: {message}",
                result.Message);

            return;
        }

        _stateMachine.Complete();

        _logger.LogInformation(
            "Print transaction completed");
    }
}