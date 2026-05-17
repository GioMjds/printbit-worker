using Microsoft.Extensions.Logging;
using PrintBit.Shared.Enums;

namespace PrintBit.Application.StateMachine;

public class TransactionStateMachine
{
    private readonly ILogger<TransactionStateMachine> _logger;

    public TransactionState CurrentState { get; private set; }
        = TransactionState.Idle;

    public decimal CurrentBalance { get; private set; }

    public TransactionStateMachine(
        ILogger<TransactionStateMachine> logger)
    {
        _logger = logger;
    }

    public void InsertCoin(decimal amount)
    {
        CurrentBalance += amount;

        CurrentState = TransactionState.WaitingForCoins;

        _logger.LogInformation(
            "Coin inserted: {amount} | Balance: {balance}",
            amount,
            CurrentBalance);

        if (CurrentBalance >= 5)
        {
            CurrentState = TransactionState.ReadyToPrint;

            _logger.LogInformation(
                "Transaction ready to print");
        }
    }

    public void StartPrinting()
    {
        CurrentState = TransactionState.Printing;

        _logger.LogInformation(
            "Printing started");
    }

    public void Complete()
    {
        CurrentState = TransactionState.Completed;

        _logger.LogInformation(
            "Transaction completed");
    }

    public void Reset()
    {
        CurrentBalance = 0;

        CurrentState = TransactionState.Idle;

        _logger.LogInformation(
            "Transaction reset");
    }
}