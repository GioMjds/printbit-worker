namespace PrintBit.Shared.Configurations;

public class IpcSettings
{
    public string PipeName { get; set; } = "printbit-node-errors";
    public int MaxMessageBytes { get; set; } = 8192;
    public string WorkerReturnPipeName { get; set; } = "printbit-worker-events";
}
