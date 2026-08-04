using MonitorVendas.Api.Common;

namespace MonitorVendas.Tests.Infrastructure;

// Fonte de aleatoriedade fixa: o delay de digitação e o jitter do envio só são
// testáveis com o ruído controlado de fora.
public sealed class FixedRandomSource(double gaussian = 1.0, double uniform = 0.5) : IRandomSource
{
    public double NextGaussian(double mean, double stdDev) => gaussian;

    public double NextDouble() => uniform;
}
