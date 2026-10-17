using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace CodeAlta.Search;

/// <summary>
/// The kernels of search by meaning: a vector is kept as 8-bit codes and one scale (symmetric, by its largest
/// component), and two vectors are compared by the dot product of their codes. arm64 uses <c>SDOT</c>, x64 AVX-VNNI
/// or AVX2, other machines a portable <see cref="Vector128"/> loop; a scalar loop is the reference of the tests.
/// </summary>
public static class SearchVectorKernels
{
    /// <summary>The largest code: codes are in [-127, 127], so that a vector and its opposite have opposite codes.</summary>
    public const int MaximumCode = 127;

    /// <summary>Gets the name of the kernel this machine uses, for diagnostics.</summary>
    public static string Name =>
        Dp.IsSupported ? "arm64-sdot"
        : AvxVnni.IsSupported ? "x64-avx-vnni"
        : Avx2.IsSupported ? "x64-avx2"
        : Vector128.IsHardwareAccelerated ? "vector128"
        : "scalar";

    /// <summary>Quantizes a vector to 8-bit codes: <c>code = round(value / scale)</c>, with <c>scale = max |value| / 127</c>.</summary>
    /// <param name="vector">The vector.</param>
    /// <param name="codes">Receives the codes: at least as long as <paramref name="vector"/>.</param>
    /// <returns>The scale; zero for a vector of zeros, whose codes are zeros.</returns>
    /// <exception cref="ArgumentException"><paramref name="codes"/> is shorter than <paramref name="vector"/>, or a value is not finite.</exception>
    public static float Quantize(ReadOnlySpan<float> vector, Span<sbyte> codes)
    {
        if (codes.Length < vector.Length) throw new ArgumentException("The codes are shorter than the vector.", nameof(codes));
        var max = 0f;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value)) throw new ArgumentException("A value of the vector is not finite.", nameof(vector));
            max = Math.Max(max, Math.Abs(value));
        }

        if (max == 0)
        {
            codes[..vector.Length].Clear();
            return 0;
        }

        var inverse = MaximumCode / max;
        for (var i = 0; i < vector.Length; i++)
        {
            codes[i] = (sbyte)Math.Clamp((int)MathF.Round(vector[i] * inverse), -MaximumCode, MaximumCode);
        }

        return max / MaximumCode;
    }

    /// <summary>Gets the dot product of two vectors of codes.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second, of the same length.</param>
    /// <returns>The sum of the products of their codes.</returns>
    /// <exception cref="ArgumentException">The lengths differ.</exception>
    public static int Dot(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right)
    {
        if (left.Length != right.Length) throw new ArgumentException("The vectors have different lengths.", nameof(right));
        return Dot(ref MemoryMarshal.GetReference(left), ref MemoryMarshal.GetReference(right), left.Length);
    }

    /// <summary>The dot product with the best kernel of this machine; the checks of support are constants for the JIT.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Dot(ref sbyte left, ref sbyte right, int length)
    {
        if (Dp.IsSupported) return DotArm(ref left, ref right, length);
        if (AvxVnni.IsSupported) return DotAvxVnni(ref left, ref right, length);
        if (Avx2.IsSupported) return DotAvx2(ref left, ref right, length);
        if (Vector128.IsHardwareAccelerated) return DotVector128(ref left, ref right, length);
        return DotScalar(ref left, ref right, length);
    }

    /// <summary>The reference.</summary>
    internal static int DotScalar(ref sbyte left, ref sbyte right, int length)
    {
        var sum = 0;
        for (var i = 0; i < length; i++) sum += Unsafe.Add(ref left, i) * Unsafe.Add(ref right, i);
        return sum;
    }

    /// <summary>arm64 with the dot product extension: <c>SDOT</c> adds four products of bytes into each 32-bit lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int DotArm(ref sbyte left, ref sbyte right, int length)
    {
        if (!Dp.IsSupported) throw new PlatformNotSupportedException();
        var acc0 = Vector128<int>.Zero;
        var acc1 = Vector128<int>.Zero;
        var i = 0;
        for (; i <= length - 32; i += 32)
        {
            acc0 = Dp.DotProduct(acc0, Vector128.LoadUnsafe(ref left, (nuint)i), Vector128.LoadUnsafe(ref right, (nuint)i));
            acc1 = Dp.DotProduct(acc1, Vector128.LoadUnsafe(ref left, (nuint)(i + 16)), Vector128.LoadUnsafe(ref right, (nuint)(i + 16)));
        }

        if (i <= length - 16)
        {
            acc0 = Dp.DotProduct(acc0, Vector128.LoadUnsafe(ref left, (nuint)i), Vector128.LoadUnsafe(ref right, (nuint)i));
            i += 16;
        }

        var sum = Vector128.Sum(acc0 + acc1);
        for (; i < length; i++) sum += Unsafe.Add(ref left, i) * Unsafe.Add(ref right, i);
        return sum;
    }

    /// <summary>x64 with AVX2: bytes widened to 16 bits, then <c>VPMADDWD</c> adds pairs of products into 32 bits.</summary>
    internal static int DotAvx2(ref sbyte left, ref sbyte right, int length)
    {
        if (!Avx2.IsSupported) throw new PlatformNotSupportedException();
        var acc0 = Vector256<int>.Zero;
        var acc1 = Vector256<int>.Zero;
        var i = 0;
        for (; i <= length - 32; i += 32)
        {
            acc0 = Avx2.Add(acc0, Avx2.MultiplyAddAdjacent(Widen(ref left, i), Widen(ref right, i)));
            acc1 = Avx2.Add(acc1, Avx2.MultiplyAddAdjacent(Widen(ref left, i + 16), Widen(ref right, i + 16)));
        }

        if (i <= length - 16)
        {
            acc0 = Avx2.Add(acc0, Avx2.MultiplyAddAdjacent(Widen(ref left, i), Widen(ref right, i)));
            i += 16;
        }

        var sum = Vector256.Sum(acc0 + acc1);
        for (; i < length; i++) sum += Unsafe.Add(ref left, i) * Unsafe.Add(ref right, i);
        return sum;
    }

    /// <summary>x64 with AVX-VNNI: as AVX2, with <c>VPDPWSSD</c> doing the products of pairs and the addition in one instruction.</summary>
    internal static int DotAvxVnni(ref sbyte left, ref sbyte right, int length)
    {
        if (!AvxVnni.IsSupported) throw new PlatformNotSupportedException();
        var acc0 = Vector256<int>.Zero;
        var acc1 = Vector256<int>.Zero;
        var i = 0;
        for (; i <= length - 32; i += 32)
        {
            acc0 = AvxVnni.MultiplyWideningAndAdd(acc0, Widen(ref left, i), Widen(ref right, i));
            acc1 = AvxVnni.MultiplyWideningAndAdd(acc1, Widen(ref left, i + 16), Widen(ref right, i + 16));
        }

        if (i <= length - 16)
        {
            acc0 = AvxVnni.MultiplyWideningAndAdd(acc0, Widen(ref left, i), Widen(ref right, i));
            i += 16;
        }

        var sum = Vector256.Sum(acc0 + acc1);
        for (; i < length; i++) sum += Unsafe.Add(ref left, i) * Unsafe.Add(ref right, i);
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Widen(ref sbyte source, int offset) => Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref source, (nuint)offset));

    /// <summary>Any machine with 128-bit vectors: bytes widened to 16 bits, multiplied (at most 128 × 128), then widened again and added.</summary>
    internal static int DotVector128(ref sbyte left, ref sbyte right, int length)
    {
        var acc = Vector128<int>.Zero;
        var i = 0;
        for (; i <= length - 16; i += 16)
        {
            var (leftLow, leftHigh) = Vector128.Widen(Vector128.LoadUnsafe(ref left, (nuint)i));
            var (rightLow, rightHigh) = Vector128.Widen(Vector128.LoadUnsafe(ref right, (nuint)i));
            var (p0, p1) = Vector128.Widen(leftLow * rightLow);
            var (p2, p3) = Vector128.Widen(leftHigh * rightHigh);
            acc += (p0 + p1) + (p2 + p3);
        }

        var sum = Vector128.Sum(acc);
        for (; i < length; i++) sum += Unsafe.Add(ref left, i) * Unsafe.Add(ref right, i);
        return sum;
    }
}
