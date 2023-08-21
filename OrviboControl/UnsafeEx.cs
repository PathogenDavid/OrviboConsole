using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace OrviboControl;

public unsafe static class UnsafeEx
{
    /// <summary>Clears the specified read-only field to prevent unintentional use of disposed resources</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clear<T>([MaybeNull] in T target)
        => Unsafe.AsRef(target) = default!;

    /// <summary>Clears the specified read-only field to prevent unintentional use of disposed resources</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clear<T>([MaybeNull] in T* target)
        where T : unmanaged
    {
        fixed (T** targetP = &target)
        { *targetP = default; }
    }
}
