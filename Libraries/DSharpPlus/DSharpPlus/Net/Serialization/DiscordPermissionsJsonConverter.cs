// This file is part of the DSharpPlus project.
//
// Copyright (c) 2015 Mike Santiago
// Copyright (c) 2016-2023 DSharpPlus Contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Globalization;
using System.Numerics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DSharpPlus.Net.Serialization
{
    internal class DiscordPermissionsJsonConverter : JsonConverter<Permissions>
    {
        public override Permissions ReadJson(JsonReader reader, Type objectType, Permissions existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            // Read the value straight from the reader: building a JToken and converting it to BigInteger through the
            // serializer for every role and every channel overwrite was a large part of parsing READY on the phone.
            switch (reader.TokenType)
            {
                case JsonToken.Null:
                case JsonToken.Undefined:
                    return Permissions.None;
                case JsonToken.String:
                    return Parse((string)reader.Value);
                case JsonToken.Integer:
                    return reader.Value is BigInteger big
                        ? (Permissions)(ulong)(big & ulong.MaxValue)
                        : (Permissions)unchecked((ulong)Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture));
            }

            // anything else: the old, general way
            var token = JToken.Load(reader);
            if (token == null || token.Type == JTokenType.Null)
                return Permissions.None;
            var value = token.ToObject<BigInteger>();
            return (Permissions)(ulong)(value & ulong.MaxValue);
        }

        /// <summary>Permissions from Discord's decimal string; bits above 64 are dropped like before.</summary>
        internal static Permissions Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
                return Permissions.None;
            if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return (Permissions)value;
            return BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var big)
                ? (Permissions)(ulong)(big & ulong.MaxValue)
                : Permissions.None;
        }

        public override void WriteJson(JsonWriter writer, Permissions value, JsonSerializer serializer)
            => writer.WriteValue(((ulong)value).ToString(CultureInfo.InvariantCulture));
    }
}
