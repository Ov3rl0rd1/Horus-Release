using Horus.Domain.Models;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Horus.Application.Diagnostics
{
    /// <summary>
    /// The persistent half of <see cref="Diag"/>: a batched, size-bounded, crash-safe
    /// event log on disk.
    ///
    /// <para><b>Why a file and not the old in-memory ring.</b> The previous session log was
    /// a 500-line <c>Queue&lt;string&gt;</c> inside <c>ErrorReportingService</c>. It was
    /// lost the instant the process died — which is precisely the case it existed to
    /// explain. Everything here is chosen so that the log of a session that ended badly is
    /// still on disk when the next one starts.</para>
    ///
    /// <para><b>Batched, because the alternative is an I/O wakeup per line.</b> Records go
    /// into a bounded channel and a single writer drains it, flushing at
    /// <see cref="BatchSize"/> records or <see cref="FlushInterval"/>, whichever comes
    /// first. The same shape as Rethink's NetLogBatcher, and for the same reason: a VPN
    /// client that fsyncs on every log line spends its night writing rather than
    /// tunnelling.</para>
    ///
    /// <para><b>Dropping is better than blocking.</b> The channel is bounded with
    /// <see cref="BoundedChannelFullMode.DropOldest"/>. Under a log storm the oldest
    /// records are lost and the tunnel keeps running — the inverse trade would let the
    /// diagnostics take down the thing being diagnosed.</para>
    ///
    /// <para><b>Two generations, kept small.</b> Sessions here last weeks, so an unbounded
    /// file is a real hazard. The current file rotates to <c>.prev</c> at
    /// <see cref="MaxFileBytes"/>, giving a hard ceiling of twice that.</para>
    ///
    /// <para>Format is JSON Lines: one self-contained object per line. Appending never
    /// requires reading the file, a truncated tail costs one record rather than the
    /// document, and the archive stays greppable.</para>
    /// </summary>
    public sealed class EventLog : IDiagSink, IDisposable
    {
        private const int BatchSize = 40;
        private const int ChannelCapacity = 2000;
        private const long MaxFileBytes = 512 * 1024;

        /// <summary>Number of records kept in memory for the Settings screen and the crash flush.</summary>
        private const int RingCapacity = 400;

        private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(2500);

        /// <summary>One record. Serialised with short names — this file is written a lot.</summary>
        public readonly record struct Entry(
            long Seq,
            DateTimeOffset At,
            DiagLevel Level,
            string Category,
            string Message,
            string? Detail,
            bool UserAction)
        {
            public string ToLine() =>
                $"{At.ToLocalTime():HH:mm:ss.fff} {Level.ToString()[0]} [{Category}] {Message}" +
                (Detail is null ? "" : $" — {Detail}");
        }

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        /// <summary>
        /// Process-wide. A static singleton rather than a DI registration because the crash
        /// handler, the boot receiver and the Quick Settings tile all need it from contexts
        /// where the container may not have been built yet.
        /// </summary>
        public static EventLog? Instance { get; private set; }

        private readonly Channel<Entry> _channel;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _fileLock = new();
        private readonly Entry[] _ring = new Entry[RingCapacity];
        private readonly object _ringLock = new();

        private int _ringNext;
        private int _ringCount;
        private long _seq;
        private long _lastWrittenSeq;
        private bool _disposed;

        private EventLog()
        {
            _channel = Channel.CreateBounded<Entry>(new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            _ = Task.Run(() => DrainAsync(_cts.Token));
        }

        /// <summary>
        /// Creates the log and hooks it into <see cref="Diag"/>. Idempotent, and safe to
        /// call before the DI container exists — which is the point, since the crash
        /// handler is installed before anything else.
        /// </summary>
        public static EventLog Install()
        {
            if (Instance is not null) return Instance;

            var log = new EventLog();
            Instance = log;
            Diag.Sink = log;
            return log;
        }

        public static string CurrentPath => Path.Combine(DiagnosticPaths.LogDirectory, "events.jsonl");
        public static string PreviousPath => CurrentPath + ".prev";

        // ── IDiagSink ────────────────────────────────────────────────────────

        public void Emit(DiagLevel level, string category, string message, string? detail, bool userAction)
        {
            if (_disposed) return;

            var entry = new Entry(
                Interlocked.Increment(ref _seq),
                DateTimeOffset.UtcNow,
                level,
                category,
                message,
                detail,
                userAction);

            PushRing(entry);

            // TryWrite never blocks on a DropOldest channel, so the caller — which may be
            // on the packet path — is never held up by logging.
            _channel.Writer.TryWrite(entry);
        }

        /// <summary>
        /// Writes every record the async drain has not got to yet, synchronously.
        ///
        /// Called from the crash handler. The drain task will not be scheduled again before
        /// the process dies, so waiting for it would lose exactly the records that explain
        /// the crash. Reads from the in-memory ring rather than the channel because the
        /// channel may have handed records over already without them reaching disk.
        /// </summary>
        public void Flush()
        {
            Entry[] pending;
            lock (_ringLock)
            {
                pending = SnapshotRing().Where(e => e.Seq > Volatile.Read(ref _lastWrittenSeq)).ToArray();
            }

            if (pending.Length > 0) WriteBatch(pending);
        }

        // ── Reading ──────────────────────────────────────────────────────────

        /// <summary>Most recent records, oldest first. Backs the Settings log viewer.</summary>
        public IReadOnlyList<Entry> Tail(int count)
        {
            lock (_ringLock)
            {
                var all = SnapshotRing();
                return count >= all.Count ? all : all.Skip(all.Count - count).ToList();
            }
        }

        /// <summary>Most recent records rendered for display, oldest first.</summary>
        public IReadOnlyList<string> TailText(int count) =>
            [.. Tail(count).Select(e => e.ToLine())];

        // ── Writer ───────────────────────────────────────────────────────────

        private async Task DrainAsync(CancellationToken ct)
        {
            var batch = new List<Entry>(BatchSize);
            var reader = _channel.Reader;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Block until there is something, then take everything available up to
                    // the batch size. This is what keeps an idle tunnel from waking up:
                    // with nothing to log the task simply parks here.
                    if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false)) break;

                    // Monotonic, not DateTime.UtcNow. On a device that has been up for
                    // weeks an NTP correction moves wall-clock time, and a backwards jump
                    // would leave this window open for as long as the jump lasted — logs
                    // sitting in the buffer instead of on disk, for hours, silently.
                    var deadline = Environment.TickCount64 + (long)FlushInterval.TotalMilliseconds;

                    while (batch.Count < BatchSize && Environment.TickCount64 < deadline)
                    {
                        if (reader.TryRead(out var entry)) { batch.Add(entry); continue; }
                        if (batch.Count == 0) break;               // nothing pending, go park

                        // Have a partial batch: wait out the rest of the window rather than
                        // spinning, so a trickle of records still gets written promptly.
                        var remaining = deadline - Environment.TickCount64;
                        if (remaining <= 0) break;
                        try { await Task.Delay((int)remaining, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }

                    if (batch.Count == 0) continue;
                    WriteBatch(batch);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                // Never through Diag: that would come straight back here.
                System.Diagnostics.Debug.WriteLine($"[Horus/eventlog] drain died: {ex}");
            }
            finally
            {
                if (batch.Count > 0) { try { WriteBatch(batch); } catch { } }
            }
        }

        private void WriteBatch(IReadOnlyList<Entry> batch)
        {
            lock (_fileLock)
            {
                try
                {
                    var path = CurrentPath;
                    RotateIfLarge(path);

                    var sb = new StringBuilder(batch.Count * 160);
                    foreach (var e in batch)
                    {
                        sb.Append(JsonSerializer.Serialize(new
                        {
                            t = e.At.ToUnixTimeMilliseconds(),
                            l = e.Level.ToString(),
                            c = e.Category,
                            m = e.Message,
                            d = e.Detail,
                            u = e.UserAction ? true : (bool?)null
                        }, Json));
                        sb.Append('\n');
                    }

                    File.AppendAllText(path, sb.ToString(), Encoding.UTF8);

                    var highest = batch[^1].Seq;
                    if (highest > Volatile.Read(ref _lastWrittenSeq))
                        Volatile.Write(ref _lastWrittenSeq, highest);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Horus/eventlog] write failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Rolls the log over at the size cap, keeping one previous generation.
        ///
        /// Safe here — unlike for the native logs — because this file is only ever open for
        /// the duration of an <c>AppendAllText</c>. The native components hold their log
        /// files open, so renaming those out from under them does not do what it looks like
        /// it does; see <see cref="DiagnosticPaths"/>.
        /// </summary>
        private static void RotateIfLarge(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < MaxFileBytes) return;

                var prev = PreviousPath;
                if (File.Exists(prev)) File.Delete(prev);
                File.Move(path, prev);
            }
            catch { /* rotation is housekeeping; never fail a write over it */ }
        }

        // ── Ring ─────────────────────────────────────────────────────────────

        private void PushRing(Entry entry)
        {
            lock (_ringLock)
            {
                _ring[_ringNext] = entry;
                _ringNext = (_ringNext + 1) % RingCapacity;
                if (_ringCount < RingCapacity) _ringCount++;
            }
        }

        /// <summary>Ring contents oldest-first. Caller holds <see cref="_ringLock"/>.</summary>
        private List<Entry> SnapshotRing()
        {
            var result = new List<Entry>(_ringCount);
            var start = _ringCount == RingCapacity ? _ringNext : 0;
            for (int i = 0; i < _ringCount; i++)
                result.Add(_ring[(start + i) % RingCapacity]);
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _channel.Writer.TryComplete();
            Flush();
            _cts.Cancel();
            _cts.Dispose();

            if (ReferenceEquals(Diag.Sink, this)) Diag.Sink = null;
            if (ReferenceEquals(Instance, this)) Instance = null;
        }
    }
}
