using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace Golos
{
    // state.bin — общая память с APO. Раскладка = apo/shared.h, менять синхронно.
    sealed unsafe class Shm : IDisposable
    {
        public const uint Magic = 0x534F4C47;
        public const int Version = 1;
        public const int Size = 139264;
        public const int OffParams = 64;
        public const int OffMeters = 1024;
        public const int RingSize = 16384;
        public const int OffRingPre = 2048;
        public const int OffRingPost = OffRingPre + RingSize * 4;
        public const int ParamCount = 128;
        public const int OffSlots = OffRingPost + RingSize * 4;   // таблица «своих настроек» микрофонов
        public const int Slots = 8;
        public const int OffSlotParams = OffSlots + 128;
        public const int SlotStride = 576;

        // какой блок параметров читаем и пишем: -1 — общий, иначе слот микрофона
        public int Slot { get; set; } = -1;
        int BlockOff => Slot < 0 ? OffParams : OffSlotParams + Slot * SlotStride;

        public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Golos", "state.bin");

        MemoryMappedFile mmf;
        MemoryMappedViewAccessor view;
        byte* p;

        public static Shm TryOpen()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var fs = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < Size) { fs.Dispose(); return null; }
                var s = new Shm();
                s.mmf = MemoryMappedFile.CreateFromFile(fs, null, Size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
                s.view = s.mmf.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
                byte* ptr = null;
                s.view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                s.p = ptr + s.view.PointerOffset;
                if (*(uint*)s.p != Magic || *(int*)(s.p + 4) != Version || *(int*)(s.p + 8) != Size)
                {
                    s.Dispose();
                    return null;
                }
                return s;
            }
            catch (Exception ex)
            {
                Log.Write("shm open: " + ex.Message);
                return null;
            }
        }

        public float[] ReadParams()
        {
            var v = new float[ParamCount];
            int* seq = (int*)(p + BlockOff);
            for (int attempt = 0; attempt < 100; attempt++)
            {
                int s1 = Volatile.Read(ref *seq);
                if ((s1 & 1) != 0) { Thread.Yield(); continue; }
                float* src = (float*)(p + BlockOff + 8);
                for (int i = 0; i < ParamCount; i++) v[i] = src[i];
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref *seq) == s1) break;
            }
            return v;
        }

        // seqlock: нечётный seq — идёт запись; APO в этот момент держит старые параметры
        // возвращает новый seq — по нему окно отличает свои правки от чужих (плагин Loupedeck)
        public int WriteParams(float[] v)
        {
            int* seq = (int*)(p + BlockOff);
            int s = Volatile.Read(ref *seq);
            if ((s & 1) != 0) s++;
            Volatile.Write(ref *seq, s + 1);
            Interlocked.MemoryBarrier();
            *(int*)(p + BlockOff + 4) = ParamCount;
            float* dst = (float*)(p + BlockOff + 8);
            for (int i = 0; i < ParamCount; i++) dst[i] = v[i];
            Interlocked.MemoryBarrier();
            Volatile.Write(ref *seq, s + 2);
            return s + 2;
        }

        public int ReadSeq() => Volatile.Read(ref *(int*)(p + BlockOff));

        // ── свои настройки микрофонов (apo/shared.h: ShmSlots) ──

        // FNV-1a по GUID endpoint в верхнем регистре, как epHashOf() в APO
        public static uint EpHash(string guid)
        {
            uint h = 2166136261u;
            foreach (var ch in guid.ToUpperInvariant()) h = (h ^ (byte)ch) * 16777619u;
            return h == 0 ? 1 : h;
        }

        public int FindSlot(uint ep)
        {
            byte* t = p + OffSlots + 16;
            for (int i = 0; i < Slots; i++)
                if (*(int*)(t + i * 8 + 4) != 0 && *(uint*)(t + i * 8) == ep) return i;
            return -1;
        }

        // новый слот с начальными параметрами; -1 — мест нет
        public int CreateSlot(uint ep, float[] init)
        {
            int found = FindSlot(ep);
            if (found >= 0) return found;
            byte* t = p + OffSlots + 16;
            for (int i = 0; i < Slots; i++)
            {
                if (*(int*)(t + i * 8 + 4) != 0) continue;
                int keep = Slot;
                Slot = i;
                WriteParams(init);
                Slot = keep;
                Volatile.Write(ref *(uint*)(t + i * 8), ep);
                Interlocked.MemoryBarrier();
                Volatile.Write(ref *(int*)(t + i * 8 + 4), 1);
                return i;
            }
            return -1;
        }

        public void DeleteSlot(uint ep)
        {
            int i = FindSlot(ep);
            if (i < 0) return;
            byte* t = p + OffSlots + 16;
            Volatile.Write(ref *(int*)(t + i * 8 + 4), 0);
            Volatile.Write(ref *(uint*)(t + i * 8), 0u);
        }

        // чьи индикаторы писать APO (0 — любого)
        public void SetMeterEp(uint ep) => Volatile.Write(ref *(uint*)(p + OffSlots + 8), ep);

        // state.bin переживает перезагрузку, а GetTickCount64 — нет: пульс «из будущего» не даёт ни одному
        // экземпляру APO снова взять индикаторы. Такой пульс просто обнуляем.
        public void ClearStaleHeartbeat()
        {
            long hb = Volatile.Read(ref *(long*)(p + OffMeters));
            if (hb > Environment.TickCount64 + 1000) Interlocked.CompareExchange(ref *(long*)(p + OffMeters), 0, hb);
        }

        public struct Meters
        {
            public long HeartbeatMs;
            public int Writer;
            public uint RingPos;
            public float SampleRate;
            public int Channels;
            public float GateDb, CompGrDb, DeessGrDb, LimGrDb;
            public int Instances;
            public int ProcessCount;
            public float[] MbIn, MbGr;
            public float TuneHz, TuneMidi, TuneTarget, TuneShift;
        }

        public Meters ReadMeters()
        {
            byte* m = p + OffMeters;
            return new Meters
            {
                HeartbeatMs = Volatile.Read(ref *(long*)m),
                Writer = *(int*)(m + 8),
                RingPos = (uint)Volatile.Read(ref *(int*)(m + 12)),
                SampleRate = *(float*)(m + 16),
                Channels = *(int*)(m + 20),
                GateDb = *(float*)(m + 24),
                CompGrDb = *(float*)(m + 28),
                DeessGrDb = *(float*)(m + 32),
                LimGrDb = *(float*)(m + 36),
                Instances = *(int*)(m + 40),
                ProcessCount = *(int*)(m + 48),
                MbIn = new[] { *(float*)(m + 60), *(float*)(m + 64), *(float*)(m + 68), *(float*)(m + 72) },
                MbGr = new[] { *(float*)(m + 76), *(float*)(m + 80), *(float*)(m + 84), *(float*)(m + 88) },
                TuneHz = *(float*)(m + 92),
                TuneMidi = *(float*)(m + 96),
                TuneTarget = *(float*)(m + 100),
                TuneShift = *(float*)(m + 104),
            };
        }

        // count отсчётов кольца, заканчивающихся на позиции end (не включая)
        public void CopyRing(bool post, uint end, int count, float[] dst, int dstOffset = 0)
        {
            float* ring = (float*)(p + (post ? OffRingPost : OffRingPre));
            uint start = end - (uint)count;
            for (int i = 0; i < count; i++) dst[dstOffset + i] = ring[(start + (uint)i) & (RingSize - 1)];
        }

        public void Dispose()
        {
            if (view != null)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                view.Dispose();
                view = null;
            }
            mmf?.Dispose();
            mmf = null;
            p = null;
        }
    }
}
