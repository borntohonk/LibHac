﻿using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LibHac.Common.FixedArrays
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Array3<T>
    {
        public const int Length = 3;

        private T _item1;
        private T _item2;
        private T _item3;

        public ref T this[int i] => ref Items[i];

        public Span<T> Items => SpanHelpers.CreateSpan(ref _item1, Length);
        public readonly ReadOnlySpan<T> ReadOnlyItems => SpanHelpers.CreateReadOnlySpan(in _item1, Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator ReadOnlySpan<T>(in Array3<T> value) => value.ReadOnlyItems;
    }
}