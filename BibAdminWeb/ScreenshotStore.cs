using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace BibAdminWeb
{
    public static class ScreenshotStore
    {
        private static readonly ConcurrentDictionary<string, int> _viewers = new();
        private static readonly ConcurrentDictionary<string, (byte[] Data, DateTime At)> _frames = new();

        public static int AddViewer(string pcNumber)
            => _viewers.AddOrUpdate(pcNumber, 1, (_, old) => old + 1);

        public static int RemoveViewer(string pcNumber)
        {
            while (true)
            {
                if (!_viewers.TryGetValue(pcNumber, out var cur)) return 0;
                var next = cur - 1;
                if (next <= 0)
                {
                    if (_viewers.TryRemove(pcNumber, out _))
                    {
                        _frames.TryRemove(pcNumber, out _);
                        return 0;
                    }
                }
                else
                {
                    if (_viewers.TryUpdate(pcNumber, next, cur)) return next;
                }
            }
        }

        public static int GetViewerCount(string pcNumber)
            => _viewers.TryGetValue(pcNumber, out var c) ? c : 0;

        public static void StoreFrame(string pcNumber, byte[] jpeg)
            => _frames[pcNumber] = (jpeg, DateTime.UtcNow);

        public static (byte[]? Data, DateTime At) GetFrame(string pcNumber)
            => _frames.TryGetValue(pcNumber, out var f) ? f : (null, DateTime.MinValue);

        public static IEnumerable<string> GetActiveStreams() => _viewers.Keys;
    }
}
