using System.Runtime.InteropServices;

namespace Miningcore.Native;

/// <summary>
/// Quantus QPoW hash (Poseidon2, hash_squeeze_twice over mining_hash || nonce).
/// Backed by libquantus.so built from Native/libquantus (Rust, qp-poseidon-core =3.1.0).
/// </summary>
public static unsafe class QuantusBindings
{
    [DllImport("libquantus", EntryPoint = "quantus_hash", CallingConvention = CallingConvention.Cdecl)]
    private static extern void quantus_hash(byte* miningHash, byte* nonce, byte* output);

    /// <param name="miningHash">32-byte header pre-hash from the node</param>
    /// <param name="nonce">64-byte big-endian nonce</param>
    /// <param name="output">64-byte hash, big-endian U512</param>
    public static void Hash(ReadOnlySpan<byte> miningHash, ReadOnlySpan<byte> nonce, Span<byte> output)
    {
        if(miningHash.Length != 32)
            throw new ArgumentException("mining hash must be 32 bytes", nameof(miningHash));
        if(nonce.Length != 64)
            throw new ArgumentException("nonce must be 64 bytes", nameof(nonce));
        if(output.Length < 64)
            throw new ArgumentException("output must be at least 64 bytes", nameof(output));

        fixed(byte* mh = miningHash)
        fixed(byte* n = nonce)
        fixed(byte* o = output)
        {
            quantus_hash(mh, n, o);
        }
    }
}
