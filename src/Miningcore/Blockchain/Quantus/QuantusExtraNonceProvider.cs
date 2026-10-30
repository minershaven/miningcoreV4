namespace Miningcore.Blockchain.Quantus;

public class QuantusExtraNonceProvider : ExtraNonceProviderBase
{
    public QuantusExtraNonceProvider(string poolId, byte? clusterInstanceId)
        : base(poolId, QuantusConstants.ExtraNonceLength, clusterInstanceId)
    {
    }
}
