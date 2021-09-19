/*
 *  File:   TrackEndEvent.cs
 *  Author: Angelo Breuer
 *
 *  The MIT License (MIT)
 *
 *  Copyright (c) Angelo Breuer 2021
 *
 *  Permission is hereby granted, free of charge, to any person obtaining a copy
 *  of this software and associated documentation files (the "Software"), to deal
 *  in the Software without restriction, including without limitation the rights
 *  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 *  copies of the Software, and to permit persons to whom the Software is
 *  furnished to do so, subject to the following conditions:
 *
 *  The above copyright notice and this permission notice shall be included in
 *  all copies or substantial portions of the Software.
 *
 *  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 *  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 *  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 *  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 *  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 *  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 *  THE SOFTWARE.
 */

namespace Lavalink4NET.Payloads.Events;

using Lavalink4NET.Player;
using Newtonsoft.Json;

/// <summary>
///     The strongly-typed representation of a track end event received from the lavalink node
///     (in serialized JSON format). For more reference see https://github.com/freyacodes/Lavalink/blob/master/IMPLEMENTATION.md
/// </summary>
public sealed class TrackEndEvent : EventPayload
{
    /// <summary>
    ///     Gets the event type.
    /// </summary>
    [JsonRequired, JsonProperty("type")]
    public override EventType Type => EventType.TrackEnd;

    /// <summary>
    ///     Gets the identifier of the track that has ended.
    /// </summary>
    [JsonRequired, JsonProperty("track")]
    public string TrackIdentifier { get; internal set; } = null!;

    /// <summary>
    ///     Gets the reason why the track ended.
    /// </summary>
    [JsonRequired, JsonProperty("reason")]
    public TrackEndReason Reason { get; internal set; }
}
