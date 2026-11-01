// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

internal sealed class BoundedTextBuffer(int maxLength)
{
    private readonly StringBuilder _head = new();
    private readonly Queue<char> _tail = new();
    private readonly StringBuilder _pendingWhitespace = new();
    private bool _hasLine;
    private long _totalLength;
    private long _pendingWhitespaceLength;

    public long TrimmedLength => _totalLength - _pendingWhitespaceLength;

    public void AppendLine(string line)
    {
        if (_hasLine)
        {
            Append(Environment.NewLine);
        }

        _hasLine = true;
        Append(line);
    }

    public string? GetHeadValue()
    {
        var trimmedLength = TrimmedLength;
        if (trimmedLength == 0)
        {
            return null;
        }

        var headLength = (int)Math.Min(trimmedLength, _head.Length);
        return RetentionLimits.TruncateHead(_head.ToString(0, headLength), trimmedLength, maxLength);
    }

    public string? GetHeadTailValue()
    {
        var length = TrimmedLength;
        if (length == 0)
        {
            return null;
        }

        var headLength = (int)Math.Min(length, _head.Length);
        var head = _head.ToString(0, headLength);
        return length <= maxLength
            ? head
            : RetentionLimits.TruncateHeadTail(head, GetSuffix(maxLength), length, maxLength);
    }

    public string GetPrefix(int length) => length == 0 ? string.Empty : _head.ToString(0, Math.Min(length, _head.Length));

    public string GetSuffix(int length)
    {
        if (length == 0)
        {
            return string.Empty;
        }

        if (TrimmedLength <= _head.Length)
        {
            return _head.ToString(0, (int)TrimmedLength)[^length..];
        }

        var characters = _tail.ToArray();
        var available = (int)Math.Min(TrimmedLength, characters.Length);
        var count = Math.Min(length, available);
        return new string(characters, available - count, count);
    }

    public void Reset()
    {
        _ = _head.Clear();
        _tail.Clear();
        _ = _pendingWhitespace.Clear();
        _hasLine = false;
        _totalLength = 0;
        _pendingWhitespaceLength = 0;
    }

    private void Append(string text)
    {
        foreach (var character in text)
        {
            _totalLength++;
            if (_head.Length < maxLength)
            {
                _ = _head.Append(character);
            }

            if (char.IsWhiteSpace(character))
            {
                _pendingWhitespaceLength++;
                if (_pendingWhitespace.Length == maxLength)
                {
                    _ = _pendingWhitespace.Remove(0, 1);
                }

                _ = _pendingWhitespace.Append(character);
                continue;
            }

            if (_pendingWhitespace.Length > 0)
            {
                foreach (var whitespace in _pendingWhitespace.ToString())
                {
                    AppendTail(whitespace);
                }

                _ = _pendingWhitespace.Clear();
            }
            _pendingWhitespaceLength = 0;
            AppendTail(character);
        }
    }

    private void AppendTail(char character)
    {
        _tail.Enqueue(character);
        if (_tail.Count > maxLength)
        {
            _ = _tail.Dequeue();
        }
    }
}
