using MonitorVendas.Api.Features.Metrics;

namespace MonitorVendas.Tests.Metrics;

public class FirstResponseBucketsTests
{
    // Cada tempo cai na faixa correta (limite superior inclusivo).
    // Faixas: 0=(0,1] 1=(1,2] 2=(2,5] 3=(5,10] 4=(10,20] 5=(20,30] 6=(30,60]
    //         7=(60,120] 8=(120,240] 9=(240,480] 10=480+
    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(1, 0)]
    [InlineData(1.5, 1)]
    [InlineData(30, 5)]
    [InlineData(31, 6)]
    [InlineData(5000, 10)]
    public void IndexOf_MapsMinutesToBucket(double minutes, int expectedIndex)
    {
        Assert.Equal(expectedIndex, FirstResponseBuckets.IndexOf(minutes));
    }

    // Histograma vazio não tem mediana.
    [Fact]
    public void EstimateMedian_EmptyHistogram_IsNull()
    {
        Assert.Null(FirstResponseBuckets.EstimateMedian(new int[FirstResponseBuckets.Count]));
    }

    // Todas as amostras numa faixa só: a mediana cai no meio dela (faixa 5–10 → 7,5).
    [Fact]
    public void EstimateMedian_SingleBucket_InterpolatesMiddle()
    {
        var histogram = new int[FirstResponseBuckets.Count];
        histogram[3] = 4; // faixa (5, 10]

        var median = FirstResponseBuckets.EstimateMedian(histogram);

        Assert.NotNull(median);
        Assert.InRange(median!.Value, 5, 10);
    }

    // Com massa distribuída, a estimativa fica na faixa onde está o elemento do meio.
    [Fact]
    public void EstimateMedian_FindsBucketOfMiddleElement()
    {
        var histogram = new int[FirstResponseBuckets.Count];
        histogram[0] = 2;  // <= 1 min
        histogram[5] = 2;  // faixa (20, 30]
        histogram[10] = 1; // 8h+

        var median = FirstResponseBuckets.EstimateMedian(histogram);

        Assert.NotNull(median);
        Assert.InRange(median!.Value, 20, 30);
    }

    // A última faixa é aberta (8h+): a estimativa devolve o piso, sem inventar teto.
    [Fact]
    public void EstimateMedian_OpenEndedBucket_ReturnsLowerBound()
    {
        var histogram = new int[FirstResponseBuckets.Count];
        histogram[10] = 3;

        Assert.Equal(480, FirstResponseBuckets.EstimateMedian(histogram));
    }
}
