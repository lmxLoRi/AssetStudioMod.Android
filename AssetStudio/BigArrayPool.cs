using System.Buffers;

namespace AssetStudio
{
    public static class BigArrayPool<T>
    {
        public static ArrayPool<T> Shared { get; }

        static BigArrayPool()
        {
            // ArrayPool.Create builds a bucket per power of two up to maxArrayLength and keeps up to
            // maxArraysPerBucket arrays in each, so the retention ceiling is roughly
            // 2 * maxArrayLength * maxArraysPerBucket. At 256 MB x 5 that is ~2.5 GB, and it was
            // reached: exporting textures on four workers put 3.0 GB of private memory in the
            // process, because each worker feeds a different bucket at the same time.
            //
            // 32 MB x 2 is enough for the textures that matter (2048x2048 Bgra32 is 16 MB) and
            // bounds retention to ~128 MB. Larger buffers simply are not pooled.
            Shared = ArrayPool<T>.Create(32 * 1024 * 1024, 2);
        }
    }
}
