namespace PrintBit.Shared.Configurations;

public class IpcSettings
{
    public string PipeName { get; set; } = "printbit-node-errors";
    public int MaxMessageBytes { get; set; } = 8192;
    public string WorkerReturnPipeName { get; set; } = "printbit-worker-events";

    // Time to wait for the Node.js listener when opening the named pipe.
    // 3 seconds is generous enough for cold start (Node.js booting alongside
    // the worker) and short enough that a stuck listener surfaces quickly.
    public int ConnectTimeoutMs { get; set; } = 3_000;
}
