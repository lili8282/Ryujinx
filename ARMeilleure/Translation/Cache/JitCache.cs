using ARMeilleure.CodeGen;
using ARMeilleure.CodeGen.Unwinding;
using ARMeilleure.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ARMeilleure.Translation.Cache
{
    static class JitCache
    {
        private const int PageSize = 4 * 1024;
        private const int PageMask = PageSize - 1;

        private const int CodeAlignment = 4; // Bytes.
        private const int CacheSize = 2047 * 1024 * 1024;

        private static ReservedRegion _jitRegion;

        private static CacheMemoryAllocator _cacheAllocator;

        private static readonly List<CacheEntry> _cacheEntries = new List<CacheEntry>();

        private static readonly object _lock = new object();
        private static bool _initialized;

        public static void Initialize(IJitMemoryAllocator allocator)
        {
            if (_initialized) return;

            lock (_lock)
            {
                if (_initialized) return;

                _jitRegion = new ReservedRegion(allocator, CacheSize);

                _cacheAllocator = new CacheMemoryAllocator(CacheSize);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    JitUnwindWindows.InstallFunctionTableHandler(_jitRegion.Pointer, CacheSize, _jitRegion.Pointer + Allocate(PageSize));
                }

                _initialized = true;
            }
        }

        public static IntPtr Map(in CompiledFunction func)
        {
            byte[] code = func.Code;

            lock (_lock)
            {
                Debug.Assert(_initialized);

                int funcOffset = Allocate(code.Length);

                IntPtr funcPtr = _jitRegion.Pointer + funcOffset;

                ReprotectAsWritable(funcOffset, code.Length);

                Marshal.Copy(code, 0, funcPtr, code.Length);

                ReprotectAsExecutable(funcOffset, code.Length);

                Add(funcOffset, code.Length, func.UnwindInfo);

                return funcPtr;
            }
        }

        public static void Unmap(IntPtr pointer)
        {
            lock (_lock)
            {
                Debug.Assert(_initialized);

                int funcOffset = (int)(pointer.ToInt64() - _jitRegion.Pointer.ToInt64());

                bool result = TryFind(funcOffset, out CacheEntry entry);
                Debug.Assert(result);

                _cacheAllocator.Free(funcOffset, AlignCodeSize(entry.Size));

                Remove(funcOffset);
            }
        }

        private static void ReprotectAsWritable(int offset, int size)
        {
            int endOffs = offset + size;

            int regionStart = offset & ~PageMask;
            int regionEnd = (endOffs + PageMask) & ~PageMask;

            _jitRegion.Block.MapAsRwx((ulong)regionStart, (ulong)(regionEnd - regionStart));
        }

        private static void ReprotectAsExecutable(int offset, int size)
        {
            int endOffs = offset + size;

            int regionStart = offset & ~PageMask;
            int regionEnd = (endOffs + PageMask) & ~PageMask;

            _jitRegion.Block.MapAsRx((ulong)regionStart, (ulong)(regionEnd - regionStart));
        }

        private static int Allocate(int codeSize)
        {
            codeSize = AlignCodeSize(codeSize);

            int allocOffset = _cacheAllocator.Allocate(codeSize);

            if (allocOffset < 0)
            {
                throw new OutOfMemoryException("JIT Cache exhausted.");
            }

            _jitRegion.ExpandIfNeeded((ulong)allocOffset + (ulong)codeSize);

            return allocOffset;
        }

        private static int AlignCodeSize(int codeSize)
        {
            return checked(codeSize + (CodeAlignment - 1)) & ~(CodeAlignment - 1);
        }

        private static void Add(int offset, int size, in UnwindInfo unwindInfo)
        {
            int index = BinarySearch(_cacheEntries, offset);

            if (index < 0)
            {
                index = ~index;
            }

            CacheEntry entry = new CacheEntry(offset, size, unwindInfo);
            _cacheEntries.Insert(index, entry);
        }

        private static void Remove(int offset)
        {
            int index = BinarySearch(_cacheEntries, offset);

            if (index < 0)
            {
                index = ~index - 1;
            }

            if (index >= 0)
            {
                _cacheEntries.RemoveAt(index);
            }
        }

        public static bool TryFind(int offset, out CacheEntry entry)
        {
            lock (_lock)
            {
                int index = BinarySearch(_cacheEntries, offset);

                if (index < 0)
                {
                    index = ~index - 1;
                }

                if (index >= 0)
                {
                    entry = _cacheEntries[index];
                    return true;
                }
            }

            entry = default;
            return false;
        }

        /// <summary>
        /// Performs binary search on the internal list of items.
        /// This implementation is specialized to support CacheEntry being a readonly struct
        /// </summary>
        /// <param name="address">Address to find</param>
        /// <returns>List index of the item, or complement index of nearest item with lower value on the list</returns>
        private static int BinarySearch(IList<CacheEntry> list, int offset)
        {
            int left = 0;
            int right = list.Count - 1;

            while (left <= right)
            {
                int range = right - left;

                int middle = left + (range >> 1);

                var item = list[middle];

                int result = item.Offset.CompareTo(offset);
                if (result == 0)
                {
                    return middle;
                }

                if (result < 0)
                {
                    right = middle - 1;
                }
                else
                {
                    left = middle + 1;
                }
            }

            return ~left;
        }
    }
}