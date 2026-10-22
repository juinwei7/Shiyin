using System.Runtime.InteropServices;

namespace Shiyin.Model;

/// <summary>聲紋（M§21.4）：說話者辨識給的向量，存成 Float32 little endian（Windows 的模型維度可能跟 Mac 不同，見 design-windows §9）</summary>
public static class Voiceprint
{
    public static byte[] Data(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    public static float[] Vector(byte[] data) => MemoryMarshal.Cast<byte, float>(data.AsSpan(0, data.Length - data.Length % 4)).ToArray();

    /// <summary>cosine 相似度，-1～1；長度不同（不同的模型）是 0</summary>
    public static double Similarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        if (na <= 0 || nb <= 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
