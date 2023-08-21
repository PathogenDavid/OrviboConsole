using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace OrviboControl
{
    public readonly struct MacAddress : IEquatable<MacAddress>, IComparable<MacAddress>
    {
        private readonly ulong Address;

        public MacAddress(ReadOnlySpan<byte> address)
        {
            if (address.Length != 6)
            { throw new ArgumentException("The address must be 6 bytes long!", nameof(address)); }

            Address = 0;
            Address |= ((ulong)address[0]) << 0;
            Address |= ((ulong)address[1]) << 8;
            Address |= ((ulong)address[2]) << 16;
            Address |= ((ulong)address[3]) << 24;
            Address |= ((ulong)address[4]) << 32;
            Address |= ((ulong)address[5]) << 40;
        }

        public void CopyTo(Span<byte> buffer)
        {
            if (buffer.Length < 6)
            { throw new ArgumentException("The buffer is too small to contain a MAC address!", nameof(buffer)); }

            buffer[0] = (byte)((Address >> 0) & 0xFF);
            buffer[1] = (byte)((Address >> 8) & 0xFF);
            buffer[2] = (byte)((Address >> 16) & 0xFF);
            buffer[3] = (byte)((Address >> 24) & 0xFF);
            buffer[4] = (byte)((Address >> 32) & 0xFF);
            buffer[5] = (byte)((Address >> 40) & 0xFF);
        }

        public static bool operator ==(MacAddress a, MacAddress b)
            => a.Equals(b);

        public static bool operator !=(MacAddress a, MacAddress b)
            => !a.Equals(b);

        public int CompareTo(MacAddress other)
            => this.Address.CompareTo(other.Address);

        public bool Equals(MacAddress other)
            => this.Address == other.Address;

        public override bool Equals([NotNullWhen(true)] object? obj)
            => obj is MacAddress other ? Equals(other) : false;

        public override int GetHashCode()
            => Address.GetHashCode();

        public override string ToString()
            => $"{(Address >> 0) & 0xFF:X02}:{(Address >> 8) & 0xFF:X02}:{(Address >> 16) & 0xFF:X02}:{(Address >> 24) & 0xFF:X02}:{(Address >> 32) & 0xFF:X02}:{(Address >> 40) & 0xFF:X02}";

        public string ToString(bool useSeparators)
            => useSeparators ? ToString() : $"{(Address >> 0) & 0xFF:X02}{(Address >> 8) & 0xFF:X02}{(Address >> 16) & 0xFF:X02}{(Address >> 24) & 0xFF:X02}{(Address >> 32) & 0xFF:X02}{(Address >> 40) & 0xFF:X02}";

        public static bool TryParse(ReadOnlySpan<char> s, out MacAddress result)
        {
            bool hasSeparators;
            const int numBytes = 6;

            if (s.Length == (2 * numBytes))
            { hasSeparators = false; }
            else if (s.Length == ((3 * numBytes) - 1))
            { hasSeparators = true; }
            else
            {
                result = default;
                return false;
            }

            Span<byte> address = stackalloc byte[numBytes];

            for (int i = 0; i < numBytes; i++)
            {
                // Validate separators if present
                if (hasSeparators && i > 0)
                {
                    if (s[0] != ':')
                    {
                        result = default;
                        return false;
                    }

                    s = s.Slice(1);
                }

                // Parse the byte
                if (!Byte.TryParse(s.Slice(0, 2), NumberStyles.AllowHexSpecifier, null, out address[i]))
                {
                    result = default;
                    return false;
                }

                s = s.Slice(2);
            }

            Debug.Assert(s.Length == 0);
            result = new MacAddress(address);
            return true;
        }
    }
}
