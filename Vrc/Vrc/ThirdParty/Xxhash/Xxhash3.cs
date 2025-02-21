using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

/// <summary>
/// Represents an XXHash3 context
/// </summary>
public sealed class XXHash3 : IDisposable
{
    internal const int XXH_ACC_NB = (XXHash.XXH_STRIPE_LEN / sizeof(ulong));
    internal const int XXH3_MIDSIZE_MAX = 240;
    internal const int XXH_SECRET_MERGEACCS_START = 11;
    internal const int XXH_SECRET_LASTACC_START = 7;
    internal const int XXH_SECRET_DEFAULT_SIZE = 192;
    internal const int XXH3_SECRET_SIZE_MIN = 136;
    internal const int XXH3_INTERNALBUFFER_SIZE = 256;

    private ulong[] _accumulator = new ulong[8];
    private byte[] _customSecret = new byte[XXH3_SECRET.Length];
    private byte[] _buffer = new byte[XXH3_INTERNALBUFFER_SIZE];

    private int _bufferedSize;
    private bool _useSeed;
    private int _currentStripeCount;
    private int _totalLength;
    private int _stripeCountPerBlock;
    private int _secretLimit;
    private ulong _seed;
    private ulong _reserved64;
    private byte[] _externalSecret;

    private const int STREAM_BUFFER_SIZE = 64 * 1024;
    private byte[] _streamBuffer;

    private bool _isDisposed;

    // csharpier-ignore
    private static readonly byte[] XXH3_SECRET = new byte[192]
    {
        0xb8, 0xfe, 0x6c, 0x39, 0x23, 0xa4, 0x4b, 0xbe, 0x7c, 0x01, 0x81, 0x2c, 0xf7, 0x21, 0xad, 0x1c,
        0xde, 0xd4, 0x6d, 0xe9, 0x83, 0x90, 0x97, 0xdb, 0x72, 0x40, 0xa4, 0xa4, 0xb7, 0xb3, 0x67, 0x1f,
        0xcb, 0x79, 0xe6, 0x4e, 0xcc, 0xc0, 0xe5, 0x78, 0x82, 0x5a, 0xd0, 0x7d, 0xcc, 0xff, 0x72, 0x21,
        0xb8, 0x08, 0x46, 0x74, 0xf7, 0x43, 0x24, 0x8e, 0xe0, 0x35, 0x90, 0xe6, 0x81, 0x3a, 0x26, 0x4c,
        0x3c, 0x28, 0x52, 0xbb, 0x91, 0xc3, 0x00, 0xcb, 0x88, 0xd0, 0x65, 0x8b, 0x1b, 0x53, 0x2e, 0xa3,
        0x71, 0x64, 0x48, 0x97, 0xa2, 0x0d, 0xf9, 0x4e, 0x38, 0x19, 0xef, 0x46, 0xa9, 0xde, 0xac, 0xd8,
        0xa8, 0xfa, 0x76, 0x3f, 0xe3, 0x9c, 0x34, 0x3f, 0xf9, 0xdc, 0xbb, 0xc7, 0xc7, 0x0b, 0x4f, 0x1d,
        0x8a, 0x51, 0xe0, 0x4b, 0xcd, 0xb4, 0x59, 0x31, 0xc8, 0x9f, 0x7e, 0xc9, 0xd9, 0x78, 0x73, 0x64,
        0xea, 0xc5, 0xac, 0x83, 0x34, 0xd3, 0xeb, 0xc3, 0xc5, 0x81, 0xa0, 0xff, 0xfa, 0x13, 0x63, 0xeb,
        0x17, 0x0d, 0xdd, 0x51, 0xb7, 0xf0, 0xda, 0x49, 0xd3, 0x16, 0x55, 0x26, 0x29, 0xd4, 0x68, 0x9e,
        0x2b, 0x16, 0xbe, 0x58, 0x7d, 0x47, 0xa1, 0xfc, 0x8f, 0xf8, 0xb8, 0xd1, 0x7a, 0xd0, 0x31, 0xce,
        0x45, 0xcb, 0x3a, 0x8f, 0x95, 0x16, 0x04, 0x28, 0xaf, 0xd7, 0xfb, 0xca, 0xbb, 0x4b, 0x40, 0x7e,
    };

    private XXHash3() => this._streamBuffer = ArrayPool<byte>.Shared.Rent(STREAM_BUFFER_SIZE);

    #region Public Streaming API

    /// <summary>
    /// Creates a new <see cref="XXHash3"/> instance using a default seed and secret
    /// </summary>
    public static XXHash3 Create()
    {
        XXHash3 state = new();
        state.Reset(0, XXH3_SECRET);
        return state;
    }

    /// <summary>
    /// Creates a new <see cref="XXHash3"/> instance using the provided seed and the default secret
    /// </summary>
    /// <param name="seed">The seed to use</param>
    public static XXHash3 Create(ulong seed)
    {
        XXHash3 state = new();

        if (seed == 0)
        {
            state.Reset(0, XXH3_SECRET);
            return state;
        }

        if (seed != state._seed || state._externalSecret is not null)
        {
            state.InitializeCustomSecretScalar(seed);
        }

        state.Reset(seed, XXH3_SECRET);

        return state;
    }

    /// <summary>
    /// Creates a new <see cref="XXHash3"/> instance using the provided secret and the default seed
    /// </summary>
    /// <param name="secret">The secret to use</param>
    public static XXHash3 Create(ReadOnlySpan<byte> secret)
    {
        Debug.Assert(secret.Length >= XXH3_SECRET_SIZE_MIN);

        XXHash3 state = new();
        state.Reset(0, secret);
        return state;
    }

    /// <summary>
    /// Creates a new <see cref="XXHash3"/> instance using the provided seed and secret
    /// </summary>
    /// <param name="seed">The seed to use</param>
    /// <param name="secret">The secret to use</param>
    public static XXHash3 Create(ulong seed, ReadOnlySpan<byte> secret)
    {
        Debug.Assert(secret.Length >= XXH3_SECRET_SIZE_MIN);

        XXHash3 state = new();
        state.Reset(seed, secret);
        // seed can be 0
        state._useSeed = true;

        return state;
    }

    /// <summary>
    /// Computes a checksum by reading data from <paramref name="stream"/> until the end
    /// </summary>
    /// <param name="stream">The stream to read from</param>
    public ulong HashData64(Stream stream)
    {
        int bytesRead;
        while ((bytesRead = stream.Read(this._streamBuffer, 0, this._streamBuffer.Length)) > 0)
        {
            Update(this._streamBuffer.AsSpan()[..bytesRead]);
        }

        return Digest64();
    }

    #endregion

    #region Public Immediate API

    /// <summary>
    /// Computes a 64-bit hash using the default secret and seed
    /// </summary>
    /// <param name="data">The data to hash</param>
    public static ulong Hash64(ReadOnlySpan<byte> data) => Hash64(data, XXH3_SECRET, 0);

    /// <summary>
    /// Computes a 64-bit hash using the default secret and the provided seed
    /// </summary>
    /// <param name="data">The data to hash</param>
    /// <param name="seed">The seed to use</param>
    public static ulong Hash64(ReadOnlySpan<byte> data, ulong seed) =>
        Hash64(data, XXH3_SECRET, seed);

    /// <summary>
    /// Computes a 64-bit hash using the provided secret and the default seed
    /// </summary>
    /// <param name="data">The data to hash</param>
    /// <param name="secret">The secret to use</param>
    public static ulong Hash64(ReadOnlySpan<byte> data, ReadOnlySpan<byte> secret) =>
        Hash64(data, secret, 0);

    /// <summary>
    /// Computes a 64-bit hash using the provided secret and seed
    /// </summary>
    /// <param name="data">The data to hash</param>
    /// <param name="secret">The secret to use</param>
    /// <param name="seed">The seed to use</param>
    public static ulong Hash64(ReadOnlySpan<byte> data, ReadOnlySpan<byte> secret, ulong seed)
    {
        Debug.Assert(secret.Length >= XXH3_SECRET_SIZE_MIN);

        if (data.Length <= 16)
        {
            return xxh3_0to16_64(data, secret, seed);
        }
        else if (data.Length <= 128)
        {
            return xxh3_17to128_64(data, secret, seed);
        }
        else if (data.Length <= XXH3_MIDSIZE_MAX)
        {
            return xxh3_129to240_64(data, secret, seed);
        }
        else
        {
            return xxh3_hashLong_64(data, secret);
        }
    }

    #endregion

    #region Immediate Stream Public API

    /// <summary>
    /// Computes a checksum using the default seed and secret
    /// by reading data from <paramref name="stream"/> until the end
    /// </summary>
    /// <param name="stream">The stream to read from</param>
    public static ulong Hash64(Stream stream)
    {
        using XXHash3 state = Create();
        return state.HashData64(stream);
    }

    /// <summary>
    /// Computes a checksum using the provided seed and the default secret
    /// by reading data from <paramref name="stream"/> until the end
    /// </summary>
    /// <param name="stream">The stream to read from</param>
    /// <param name="seed">The seed to use</param>
    public static ulong Hash64(Stream stream, ulong seed)
    {
        using XXHash3 state = Create(seed);
        return state.HashData64(stream);
    }

    /// <summary>
    /// Computes a checksum using the default seed and the provided secret
    /// by reading data from <paramref name="stream"/> until the end
    /// </summary>
    /// <param name="stream">The stream to read from</param>
    /// <param name="secret">The secret to use</param>
    public static ulong Hash64(Stream stream, ReadOnlySpan<byte> secret)
    {
        using XXHash3 state = Create(secret);
        return state.HashData64(stream);
    }

    /// <summary>
    /// Computes a checksum using the provided seed and secret
    /// by reading data from <paramref name="stream"/> until the end
    /// </summary>
    /// <param name="stream">The stream to read from</param>
    /// <param name="secret">The secret to use</param>
    /// <param name="seed">The seed to use</param>
    public static ulong Hash64(Stream stream, ReadOnlySpan<byte> secret, ulong seed)
    {
        using XXHash3 state = Create(seed, secret);
        return state.HashData64(stream);
    }

    #endregion

    #region XXHash3 Internal routines

    #region XXHash3 Internal Immediate routines

    private static ulong xxh3_0to16_64(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> secret,
        ulong seed
    )
    {
        if (data.Length > 8)
            return xxh3_len_9to16_64(data, secret, seed);
        else if (data.Length >= 4)
            return xxh3_len_4to8_64(data, secret, seed);
        else if (data.Length > 0)
            return xxh3_len_1to3_64(data, secret, seed);
        else
            return xxh3_avalanche(
                seed ^ (XXHash.Read64Le(secret[56..]) ^ XXHash.Read64Le(secret[64..]))
            );

        static ulong xxh3_len_9to16_64(
            ReadOnlySpan<byte> data,
            ReadOnlySpan<byte> secret,
            ulong seed
        )
        {
            ulong bitflip1 =
                (XXHash.Read64Le(secret[24..]) ^ XXHash.Read64Le(secret[32..])) + seed;
            ulong bitflip2 =
                (XXHash.Read64Le(secret[40..]) ^ XXHash.Read64Le(secret[48..])) - seed;
            ulong input_low = XXHash.Read64Le(data) ^ bitflip1;
            ulong input_high = XXHash.Read64Le(data[(data.Length - 8)..]) ^ bitflip2;
            ulong acc =
                (ulong)data.Length
                + XXHash.Swap64(input_low)
                + input_high
                + xxh3_mul128_fold64(input_low, input_high);

            return xxh3_avalanche(acc);
        }

        static ulong xxh3_len_4to8_64(
            ReadOnlySpan<byte> data,
            ReadOnlySpan<byte> secret,
            ulong seed
        )
        {
            seed ^= (ulong)XXHash.Swap32((uint)seed) << 32;

            uint input1 = XXHash.Read32Le(data);
            uint input2 = XXHash.Read32Le(data[(data.Length - 4)..]);
            ulong bitflip =
                (XXHash.Read64Le(secret[8..]) ^ XXHash.Read64Le(secret[16..])) - seed;
            ulong input64 = input2 + (((ulong)input1) << 32);
            ulong keyed = input64 ^ bitflip;

            return xxh3_rrmxmx(keyed, (ulong)data.Length);
        }

        static ulong xxh3_len_1to3_64(
            ReadOnlySpan<byte> data,
            ReadOnlySpan<byte> secret,
            ulong seed
        )
        {
            byte c1 = data[0];
            byte c2 = data[data.Length >> 1];
            byte c3 = data[data.Length - 1];
            uint combined =
                ((uint)c1 << 16)
                | ((uint)c2 << 24)
                | ((uint)c3 << 0)
                | ((uint)data.Length << 8);
            ulong bitflip = (XXHash.Read32Le(secret) ^ XXHash.Read32Le(secret[4..])) + seed;
            ulong keyed = (ulong)combined ^ bitflip;

            return xxh64_avalanche(keyed);
        }
    }

    private static ulong xxh3_17to128_64(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> secret,
        ulong seed
    )
    {
        ulong acc = (ulong)data.Length * XXHash.XXH_PRIME64_1;
        ulong accEnd = 0;

        acc += xxh3_mix16B(data, secret, seed);
        accEnd += xxh3_mix16B(data[(data.Length - 16)..], secret[16..], seed);
        if (data.Length > 32)
        {
            acc += xxh3_mix16B(data[16..], secret[32..], seed);
            accEnd += xxh3_mix16B(data[(data.Length - 32)..], secret[48..], seed);

            if (data.Length > 64)
            {
                acc += xxh3_mix16B(data[32..], secret[64..], seed);
                accEnd += xxh3_mix16B(data[(data.Length - 48)..], secret[80..], seed);

                if (data.Length > 96)
                {
                    acc += xxh3_mix16B(data[48..], secret[96..], seed);
                    accEnd += xxh3_mix16B(data[(data.Length - 64)..], secret[112..], seed);
                }
            }
        }

        return xxh3_avalanche(acc + accEnd);
    }

    private static ulong xxh3_129to240_64(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> secret,
        ulong seed
    )
    {
        ulong acc = (ulong)data.Length * XXHash.XXH_PRIME64_1;

        int round_count = data.Length / 16;
        for (int i = 0; i < 8; i++)
        {
            acc += xxh3_mix16B(data[(16 * i)..], secret[(16 * i)..], seed);
        }

        acc = xxh3_avalanche(acc);

        for (int i = 8; i < round_count; i++)
        {
            acc += xxh3_mix16B(data[(16 * i)..], secret[((16 * (i - 8)) + 3)..], seed);
        }

        acc += xxh3_mix16B(data[(data.Length - 16)..], secret[(136 - 17)..], seed);

        return xxh3_avalanche(acc);
    }

    private static ulong xxh3_hashLong_64(ReadOnlySpan<byte> data, ReadOnlySpan<byte> secret)
    {
        ulong[] acc = new ulong[8]
        {
            XXHash.XXH_PRIME32_3,
            XXHash.XXH_PRIME64_1,
            XXHash.XXH_PRIME64_2,
            XXHash.XXH_PRIME64_3,
            XXHash.XXH_PRIME64_4,
            XXHash.XXH_PRIME32_2,
            XXHash.XXH_PRIME64_5,
            XXHash.XXH_PRIME32_1
        };

        int stripesPerBlock = (secret.Length - XXHash.XXH_STRIPE_LEN) / 8;
        int blockLength = XXHash.XXH_STRIPE_LEN * stripesPerBlock;
        int blockCount = (data.Length - 1) / blockLength;

        for (int n = 0; n < blockCount; n++)
        {
            xxh3_accumulate(acc, data[(n * blockLength)..], secret, stripesPerBlock);
            xxh3_scramble_acc_scalar(acc, secret[(secret.Length - XXHash.XXH_STRIPE_LEN)..]);
        }

        int stripeCount =
            ((data.Length - 1) - (blockLength * blockCount)) / XXHash.XXH_STRIPE_LEN;
        xxh3_accumulate(acc, data[(blockCount * blockLength)..], secret, stripeCount);

        ReadOnlySpan<byte> p = data[(data.Length - XXHash.XXH_STRIPE_LEN)..];
        xxh3_accumulate_512_scalar(
            acc,
            p,
            secret[(secret.Length - XXHash.XXH_STRIPE_LEN - 7)..]
        );

        return xxh3_merge_accs(
            acc,
            secret[XXH_SECRET_MERGEACCS_START..],
            (ulong)data.Length * XXHash.XXH_PRIME64_1
        );
    }

    #endregion

    #region XXHash3 Internal Streaming routines

    private void Reset(ulong seed, ReadOnlySpan<byte> secret)
    {
        this._bufferedSize = 0;
        this._useSeed = false;
        this._currentStripeCount = 0;
        this._totalLength = 0;
        this._stripeCountPerBlock = 0;
        this._secretLimit = 0;
        this._seed = 0;
        this._reserved64 = 0;
        this._externalSecret = null;

        this._accumulator = new ulong[8]
        {
            XXHash.XXH_PRIME32_3,
            XXHash.XXH_PRIME64_1,
            XXHash.XXH_PRIME64_2,
            XXHash.XXH_PRIME64_3,
            XXHash.XXH_PRIME64_4,
            XXHash.XXH_PRIME32_2,
            XXHash.XXH_PRIME64_5,
            XXHash.XXH_PRIME32_1
        };

        this._seed = seed;
        this._useSeed = seed != 0;

        Debug.Assert(secret.Length >= XXH3_SECRET_SIZE_MIN);
        this._externalSecret = new byte[secret.Length];
        secret.CopyTo(this._externalSecret);

        this._secretLimit = secret.Length - XXHash.XXH_STRIPE_LEN;
        this._stripeCountPerBlock = this._secretLimit / XXHash.XXH_SECRET_CONSUME_RATE;
    }

    private void InitializeCustomSecretScalar(ulong seed)
    {
        for (int i = 0; i < XXH3_SECRET.Length / 16; i++)
        {
            ulong low = XXHash.Read64Le(this._customSecret.AsSpan()[(i * 16)..]) + seed;
            ulong high = XXHash.Read64Le(this._customSecret.AsSpan()[(i * 16 + 8)..]) - seed;

            XXHash.Write64Le(this._customSecret.AsSpan()[(i * 16)..], low);
            XXHash.Write64Le(this._customSecret.AsSpan()[(i * 16 + 8)..], high);
        }
    }

    // https://github.com/Cyan4973/xxHash/blob/dev/xxhash.h#L5446
    private static void ConsumeStripes(
        Span<ulong> accumulator,
        ref int currentStripeCount,
        int stripeCountPerBlock,
        ReadOnlySpan<byte> input,
        int stripeCount,
        ReadOnlySpan<byte> secret,
        int secretLimit
    )
    {
        Debug.Assert(stripeCount <= stripeCountPerBlock);
        Debug.Assert(currentStripeCount < stripeCountPerBlock);

        if (stripeCountPerBlock - currentStripeCount <= stripeCount)
        {
            int stripeCountUntilEndOfBlock = stripeCountPerBlock - currentStripeCount;
            int stripeCountAfterBlock = stripeCount - stripeCountUntilEndOfBlock;

            xxh3_accumulate(
                accumulator,
                input,
                secret[(currentStripeCount * XXHash.XXH_SECRET_CONSUME_RATE)..],
                stripeCountUntilEndOfBlock
            );
            xxh3_scramble_acc(accumulator, secret[secretLimit..]);
            xxh3_accumulate(
                accumulator,
                input[(stripeCountUntilEndOfBlock * XXHash.XXH_STRIPE_LEN)..],
                secret,
                stripeCountAfterBlock
            );

            currentStripeCount = stripeCountAfterBlock;
        }
        else
        {
            xxh3_accumulate(
                accumulator,
                input,
                secret[(currentStripeCount * XXHash.XXH_SECRET_CONSUME_RATE)..],
                stripeCount
            );

            currentStripeCount += stripeCount;
        }
    }

    //https://github.com/Cyan4973/xxHash/blob/dev/xxhash.h#L5478
    private void Update(ReadOnlySpan<byte> data)
    {
        // If input is empty, return early
        if (data.IsEmpty)
        {
            return;
        }

        // Choose secret
        ReadOnlySpan<byte> secret = this._externalSecret is null
            ? this._customSecret
            : this._externalSecret;

        this._totalLength += data.Length;
        Debug.Assert(this._bufferedSize <= XXH3_INTERNALBUFFER_SIZE);

        // small input
        if (this._bufferedSize + data.Length <= XXH3_INTERNALBUFFER_SIZE)
        {
            data.CopyTo(this._buffer.AsSpan(this._bufferedSize));
            this._bufferedSize += data.Length;
            return;
        }

        int internalBufferStripeCount = XXH3_INTERNALBUFFER_SIZE / XXHash.XXH_STRIPE_LEN;
        int dataOffset = 0;
        if (this._bufferedSize != 0)
        {
            int loadSize = XXH3_INTERNALBUFFER_SIZE - this._bufferedSize;

            data[dataOffset..(dataOffset + loadSize)].CopyTo(
                this._buffer.AsSpan(this._bufferedSize)
            );
            dataOffset += loadSize;

            ConsumeStripes(
                this._accumulator,
                ref this._currentStripeCount,
                this._stripeCountPerBlock,
                this._buffer,
                internalBufferStripeCount,
                secret,
                this._secretLimit
            );

            this._bufferedSize = 0;
        }

        Debug.Assert(dataOffset < data.Length);

        // large input
        if (data.Length - dataOffset > this._stripeCountPerBlock * XXHash.XXH_STRIPE_LEN)
        {
            int stripeCount = (data.Length - 1 - dataOffset) / XXHash.XXH_STRIPE_LEN;
            Debug.Assert(this._stripeCountPerBlock >= this._currentStripeCount);

            // join to current block's end
            {
                int stripeCountUntilEnd = this._stripeCountPerBlock - this._currentStripeCount;
                Debug.Assert(stripeCountUntilEnd <= stripeCount);

                xxh3_accumulate(
                    this._accumulator,
                    data[dataOffset..],
                    secret[(this._currentStripeCount * XXHash.XXH_SECRET_CONSUME_RATE)..],
                    stripeCountUntilEnd
                );
                xxh3_scramble_acc(this._accumulator, secret[this._secretLimit..]);

                this._currentStripeCount = 0;
                dataOffset += stripeCountUntilEnd * XXHash.XXH_STRIPE_LEN;
                stripeCount -= stripeCountUntilEnd;
            }

            // consume per entire blocks
            while (stripeCount >= this._stripeCountPerBlock)
            {
                xxh3_accumulate(
                    this._accumulator,
                    data[dataOffset..],
                    secret,
                    this._stripeCountPerBlock
                );
                xxh3_scramble_acc(this._accumulator, secret[this._secretLimit..]);

                dataOffset += this._stripeCountPerBlock * XXHash.XXH_STRIPE_LEN;
                stripeCount -= this._stripeCountPerBlock;
            }

            // consume last partial block
            {
                xxh3_accumulate(this._accumulator, data[dataOffset..], secret, stripeCount);

                dataOffset += stripeCount * XXHash.XXH_STRIPE_LEN;
                Debug.Assert(dataOffset < data.Length);

                this._currentStripeCount = stripeCount;

                data.Slice(dataOffset - XXHash.XXH_STRIPE_LEN, XXHash.XXH_STRIPE_LEN)
                    .CopyTo(this._buffer.AsSpan()[^XXHash.XXH_STRIPE_LEN..]);

                Debug.Assert(data.Length - dataOffset <= XXHash.XXH_STRIPE_LEN);
            }
        }
        else
        {
            // content to consume <= block size
            // Consume input by a multiple of internal buffer size
            if (data.Length - dataOffset > XXH3_INTERNALBUFFER_SIZE)
            {
                int limit = data.Length - XXH3_INTERNALBUFFER_SIZE;
                do
                {
                    ConsumeStripes(
                        this._accumulator,
                        ref this._currentStripeCount,
                        this._stripeCountPerBlock,
                        data[dataOffset..],
                        internalBufferStripeCount,
                        secret,
                        this._secretLimit
                    );

                    dataOffset += XXH3_INTERNALBUFFER_SIZE;
                } while (dataOffset < limit);

                data.Slice(dataOffset - XXHash.XXH_STRIPE_LEN, XXHash.XXH_STRIPE_LEN)
                    .CopyTo(this._buffer.AsSpan()[^XXHash.XXH_STRIPE_LEN..]);
            }
        }

        Debug.Assert(dataOffset < data.Length);
        Debug.Assert(data.Length - dataOffset <= XXH3_INTERNALBUFFER_SIZE);
        Debug.Assert(this._bufferedSize == 0);

        data[dataOffset..].CopyTo(this._buffer);
        this._bufferedSize = data.Length - dataOffset;
    }

    private ulong Digest64()
    {
        ReadOnlySpan<byte> secret = this._externalSecret is null
            ? this._customSecret
            : this._externalSecret;
        if (this._totalLength > XXH3_MIDSIZE_MAX)
        {
            Span<ulong> accumulator = stackalloc ulong[XXH_ACC_NB];
            accumulator.Clear();

            DigestLong(accumulator, secret);
            return xxh3_merge_accs(
                accumulator,
                secret[XXH_SECRET_MERGEACCS_START..],
                (ulong)this._totalLength * XXHash.XXH_PRIME64_1
            );
        }

        return this._useSeed switch
        {
            true => Hash64(this._buffer.AsSpan(0, this._totalLength), this._seed),
            false => Hash64(this._buffer.AsSpan(0, this._totalLength), secret)
        };
    }

    //https://github.com/Cyan4973/xxHash/blob/dev/xxhash.h#L5602
    private void DigestLong(Span<ulong> accumulator, ReadOnlySpan<byte> secret)
    {
        this._accumulator.CopyTo(accumulator);

        if (this._bufferedSize >= XXHash.XXH_STRIPE_LEN)
        {
            int stripeCount = (this._bufferedSize - 1) / XXHash.XXH_STRIPE_LEN;
            int currentStripeCount = this._currentStripeCount;

            ConsumeStripes(
                accumulator,
                ref currentStripeCount,
                this._stripeCountPerBlock,
                this._buffer,
                stripeCount,
                secret,
                this._secretLimit
            );

            xxh3_accumulate_512(
                accumulator,
                this._buffer.AsSpan()[(this._bufferedSize - XXHash.XXH_STRIPE_LEN)..],
                secret[(this._secretLimit - XXH_SECRET_LASTACC_START)..]
            );
        }
        else
        {
            Span<byte> lastStripe = stackalloc byte[XXHash.XXH_STRIPE_LEN];
            lastStripe.Clear();

            int catchupSize = XXHash.XXH_STRIPE_LEN - this._bufferedSize;

            Debug.Assert(this._bufferedSize > 0);

            this._buffer.AsSpan()[^catchupSize..].CopyTo(lastStripe);
            this._buffer.AsSpan()[..this._bufferedSize].CopyTo(lastStripe[catchupSize..]);

            xxh3_accumulate_512(
                accumulator,
                lastStripe,
                secret[(this._secretLimit - XXH_SECRET_LASTACC_START)..]
            );
        }
    }

    #endregion

    #endregion

    private static ulong xxh3_merge_accs(
        Span<ulong> acc,
        ReadOnlySpan<byte> secret,
        ulong start
    )
    {
        ulong result = start;

        for (int i = 0; i < 4; i++)
        {
            result += xxh3_mix2accs(acc[(2 * i)..], secret[(16 * i)..]);
        }

        return xxh3_avalanche(result);
    }

    private static ulong xxh3_mix2accs(Span<ulong> acc, ReadOnlySpan<byte> secret) =>
        xxh3_mul128_fold64(
            acc[0] ^ XXHash.Read64Le(secret),
            acc[1] ^ XXHash.Read64Le(secret[8..])
        );

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong xxh3_avalanche(ulong hash)
    {
        hash = xxh_xorshift64(hash, 37);
        hash *= 0x165667919E3779F9UL;
        hash = xxh_xorshift64(hash, 32);

        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong xxh64_avalanche(ulong hash)
    {
        hash ^= hash >> 33;
        hash *= XXHash.XXH_PRIME64_2;
        hash ^= hash >> 29;
        hash *= XXHash.XXH_PRIME64_3;
        hash ^= hash >> 32;

        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong xxh3_rrmxmx(ulong h64, ulong len)
    {
        h64 ^= XXHash.RotLeft64(h64, 49) ^ XXHash.RotLeft64(h64, 24);
        h64 *= 0x9FB21C651E98DF25UL;
        h64 ^= (h64 >> 35) + len;
        h64 *= 0x9FB21C651E98DF25UL;

        return xxh_xorshift64(h64, 28);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong xxh3_mix16B(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> secret,
        ulong seed
    )
    {
        ulong input_low = XXHash.Read64Le(data);
        ulong input_high = XXHash.Read64Le(data[8..]);

        return xxh3_mul128_fold64(
            input_low ^ (XXHash.Read64Le(secret) + seed),
            input_high ^ (XXHash.Read64Le(secret[8..]) - seed)
        );
    }

    #region XXHash3 Accumulate

    private static unsafe void xxh3_accumulate(
        Span<ulong> acc,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> secret,
        int stripeCount
    )
    {
        for (int i = 0; i < stripeCount; i++)
        {
            xxh3_accumulate_512(acc, data[(i * XXHash.XXH_STRIPE_LEN)..], secret[(i * 8)..]);
        }
    }

    // TODO: Implement SSE2/AVX2
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void xxh3_accumulate_512(
        Span<ulong> acc,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> secret
    ) => xxh3_accumulate_512_scalar(acc, data, secret);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void xxh3_accumulate_512_scalar(
        Span<ulong> acc,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> secret
    )
    {
        for (int i = 0; i < XXH_ACC_NB; i++)
        {
            ulong data_val = XXHash.Read64Le(data[(8 * i)..]);
            ulong data_key = data_val ^ XXHash.Read64Le(secret[(i * 8)..]);

            acc[i ^ 1] += data_val;
            acc[i] += xxh_mul32to64(data_key & 0xFFFFFFFF, data_key >> 32);
        }
    }

    private static unsafe void xxh3_accumulate_512_sse2(
        ulong[] acc,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> secret
    )
    {
        //Span<Vector128<uint>> xacc = MemoryMarshal.Cast<ulong, Vector128<uint>>(acc);
        //ReadOnlySpan<Vector128<uint>> xdata = MemoryMarshal.Cast<byte, Vector128<uint>>(data);
        //ReadOnlySpan<Vector128<uint>> xsecret = MemoryMarshal.Cast<byte, Vector128<uint>>(secret);
        //
        //for (int i = 0; i < XXHash.XXH_STRIPE_LEN / 16; i++)
        //{
        //    Vector128<uint> data_vec = xdata[i];
        //    Vector128<uint> key_vec = xsecret[i];
        //
        //    Vector128<uint> data_key = Sse2.Xor(data_vec, key_vec);
        //    Vector128<uint> data_key_low = Sse2.Shuffle(data_key, _mm_shuffle(0, 3, 0, 1));
        //    Vector128<uint> product = Sse2.Multiply(data_key, data_key_low).AsUInt32();
        //
        //    Vector128<uint> data_swap = Sse2.Shuffle(data_vec, _mm_shuffle(1, 0, 3, 2));
        //    Vector128<uint> sum = Sse2.Add(xacc[i], data_swap);
        //
        //    xacc[i] = Sse2.Add(product, sum);
        //}
    }

    private static void xxh3_accumulate_512_avx2(
        ulong[] acc,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> secret
    )
    {
    }

    #endregion

    #region XXHash3 Scramble Acc

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void xxh3_scramble_acc(
        Span<ulong> accumulator,
        ReadOnlySpan<byte> secret
    ) => xxh3_scramble_acc_scalar(accumulator, secret);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void xxh3_scramble_acc_scalar(
        Span<ulong> accumulator,
        ReadOnlySpan<byte> secret
    )
    {
        for (int i = 0; i < XXH_ACC_NB; i++)
        {
            xxh3_scramble_acc_scalar_round(accumulator, secret, i);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void xxh3_scramble_acc_scalar_round(
        Span<ulong> accumulator,
        ReadOnlySpan<byte> secret,
        int lane
    )
    {
        Debug.Assert(lane < XXH_ACC_NB);

        ulong key64 = XXHash.Read64Le(secret[(8 * lane)..]);
        ulong acc64 = accumulator[lane];

        acc64 = xxh_xorshift64(acc64, 47);
        acc64 ^= key64;
        acc64 *= XXHash.XXH_PRIME32_1;

        accumulator[lane] = acc64;
    }

    // TODO
    private static void xxh3_scramble_acc_sse2(ulong[] acc, ReadOnlySpan<byte> secret)
    {
    }

    // TODO
    private static void xxh3_scramble_acc_avx2(ulong[] acc, ReadOnlySpan<byte> secret)
    {
    }

    #endregion

    #region XXHash3 bit twiddling utilities

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong xxh_xorshift64(ulong v64, int shift) => v64 ^ (v64 >> shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ulong xxh3_mul128_fold64(ulong lhs, ulong rhs)
    {
#if NETCOREAPP
        ulong low;
        ulong high = Bmi2.X64.MultiplyNoFlags(lhs, rhs, &low);

        return low ^ high;
#else
        return xxh3_mul128_fold64_slow(rhs, lhs);
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ulong xxh3_mul128_fold64_slow(ulong lhs, ulong rhs)
    {
        uint lhsHigh = (uint)(lhs >> 32);
        uint rhsHigh = (uint)(rhs >> 32);
        uint lhsLow = (uint)lhs;
        uint rhsLow = (uint)rhs;

        ulong high = xxh_mul32to64(lhsHigh, rhsHigh);
        ulong middleOne = xxh_mul32to64(lhsLow, rhsHigh);
        ulong middleTwo = xxh_mul32to64(lhsHigh, rhsLow);
        ulong low = xxh_mul32to64(lhsLow, rhsLow);

        ulong t = low + (middleOne << 32);
        ulong carry1 = t < low ? 1u : 0u;

        low = t + (middleTwo << 32);
        ulong carry2 = low < t ? 1u : 0u;
        high = high + (middleOne >> 32) + (middleTwo >> 32) + carry1 + carry2;

        return high + low;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong xxh_mul32to64(ulong x, ulong y) => (x & 0xFFFFFFFF) * (y & 0xFFFFFFFF);

    #endregion

    #region Dispose

    private void Dispose(bool disposing)
    {
        if (this._isDisposed is false)
        {
            if (disposing)
            {
                ArrayPool<byte>.Shared.Return(this._streamBuffer);
            }

            this._isDisposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    #endregion
}