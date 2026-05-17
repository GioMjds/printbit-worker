namespace PrintBit.Shared.Enums;

public enum TransactionState
{
    Idle = 0,
    WaitingForCoins,
    ReadyToPrint,
    Printing,
    DispensingChange,
    Completed,
    Error
}