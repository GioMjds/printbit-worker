using System.Threading.Channels;
using PrintBit.Hardware.Devices.ESP32;

namespace PrintBit.Application.Queues
{
    public class HardwareEventQueue
    {
        private readonly Channel<Esp32Message> _channel;

        public HardwareEventQueue()
        {
            _channel = Channel.CreateUnbounded<Esp32Message>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                });
        }

        public ValueTask EnqueueAsync(
            Esp32Message message,
            CancellationToken cancellationToken = default)
        {
            return _channel.Writer.WriteAsync(message, cancellationToken);
        }

        public IAsyncEnumerable<Esp32Message> ReadAllAsync(
            CancellationToken cancellationToken = default)
        {
            return _channel.Reader.ReadAllAsync(cancellationToken);
        }
    }
}
