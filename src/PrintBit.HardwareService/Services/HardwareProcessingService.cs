using PrintBit.Application.Queues;
using PrintBit.Application.Services;

namespace PrintBit.HardwareService.Services
{
    public class HardwareProcessingService : BackgroundService
    {
        private readonly ILogger<HardwareProcessingService> _logger;
        private readonly HardwareEventQueue _queue;
        private readonly HardwareOrchestrator _orchestrator;
        public HardwareProcessingService(
            ILogger<HardwareProcessingService> logger,
            HardwareEventQueue queue,
            HardwareOrchestrator orchestrator)
        {
            _logger = logger;
            _queue = queue;
            _orchestrator = orchestrator;
        }

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            await foreach (var message in _queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _orchestrator.HandleEsp32MessageAsync(message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Hardware event processing failed.");
                }
            }
        }
    }
}
