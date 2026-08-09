using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Globalization;
using System.Text;

namespace GmPvfLib
{
    
    
    
    
    public sealed class PvfArchive : IDisposable
    {
        private const uint MagicSignature = 0x69706B6E;
        // Decompressed script chunks can dwarf the 61MB archive; keep a hard ceiling.
        // 4MB + frequent ClearChunkCache during bulk index keeps rebuild peak low.
        private const long DefaultChunkCacheBudgetBytes = 4L * 1024 * 1024;

        private byte[] _strABuffer;
        private byte[] _strWBuffer;
        private byte[] _bodyBuffer;
        private int _bodyOffset;
        private int _bodyLength;
        // mmap mode: body stays on disk; only header/table/name/grpi live on the managed heap.
        private MemoryMappedFile _mappedFile;
        private MemoryMappedViewAccessor _mappedView;
        private long _bodyFileOffset;
        private readonly List<PvfFileData> _files = new List<PvfFileData>();
        private readonly List<GrpiItem> _groups = new List<GrpiItem>();
        private readonly Dictionary<string, int> _pathIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Compact mapped mode: keep raw file table entries without materializing 593k path strings.
        private PvfFileItem[] _fileItems;
        private bool _filesMaterialized;
        private bool _compactPathIndex;
        // Lite mapped: table+grpi+name only; path → index comes from ExternalPathResolver (disk).
        private bool _liteMapped;
        private int _liteFileCount;
        // File-table file offset for lite mmap (no 14MB managed copy of the table).
        private long _tableFileOffset;
        // Sticky path hits after ExternalPathResolver; hard-capped so long sessions cannot grow unbounded.
        private const int MaxStickyPathEntries = 512;
        // Optional process-wide path lookup (SQLite archive_paths). Avoids ~300MB in-memory path map.
        public static Func<string, int> ExternalPathResolver { get; set; }
        // Cache string offsets so new paths/tokens can reuse or extend NameTable.
        private Dictionary<string, int> _strAOffsetCache;
        private Dictionary<string, int> _strWOffsetCache;
        private PvfHashTable _hashTable;
        private bool _disposed;

        // GM runtime only needs these trees. Indexing all 593k paths costs ~300MB+ RSS.
        private static readonly string[] RuntimePathPrefixes =
        {
            "equipment/",
            "stackable/",
            "skill/",
            "character/",
            "etc/",
            "quest/",
            "n_quest/",
            "dungeon/",
            "worldmap/",
            "town/",
            "map/",
            "aicharacter/",
            "passiveobject/",
            "creature/",
            "npc/",
            "common/",
        };

        
        private PvfHeader _header;
        private bool _headerUsesGuard;
        private byte[] _rawTableBytes;   
        private int _rawTableOffset;     
        private int _rawTableSize;       
        private byte[] _rawHashBytes;    
        private byte[] _rawNameBytes;    
        private byte[] _rawGrpiBytes;    

        
        // Only changed/new file payloads live here; unchanged chunks are copied raw.
        private readonly Dictionary<int, byte[]> _overlay = new Dictionary<int, byte[]>();

        private readonly LruByteCache _chunkCache = new LruByteCache(DefaultChunkCacheBudgetBytes);

        public IReadOnlyList<PvfFileData> Files
        {
            get
            {
                EnsureFilesMaterialized();
                return _files;
            }
        }
        public int FileCount =>
            _liteMapped ? _liteFileCount
            : _fileItems != null ? _fileItems.Length
            : _files.Count;
        public PvfHashTable HashTable => _hashTable;
        internal IReadOnlyList<GrpiItem> Groups => _groups;
        /// <summary>True when opened via OpenMapped with no in-memory path index.</summary>
        public bool IsLiteMapped => _liteMapped;

        
        public bool HasModifications => _overlay.Count > 0;

        
        public int ModifiedCount => _overlay.Count;

        // Packer callers preserve the source PVF header encoding.
        internal bool HeaderUsesGuard => _headerUsesGuard;

        
        public PvfHeader GetHeader() => _header;

        
        public byte[] GetRawHashBytes() => (byte[])_rawHashBytes.Clone();

        
        public byte[] GetRawNameBytes() => (byte[])_rawNameBytes.Clone();

        
        private byte[] GetRawTableBytes()
        {
            if (_rawTableBytes == null)
            {
                if (_bodyBuffer == null)
                    throw new InvalidOperationException("mmap 模式下缺少 file table 缓存。");
                _rawTableBytes = _bodyBuffer.Slice(_rawTableOffset, _rawTableSize);
            }
            return (byte[])_rawTableBytes.Clone();
        }

        private PvfArchive() { }

        private bool IsMapped => _mappedView != null;

        
        
        
        public byte[] ToBytes()
        {
            
            byte[] tableBytes = GetRawTableBytes();
            byte[] nameBytes = (byte[])_rawNameBytes.Clone();

            byte[] hashBytes = (byte[])_rawHashBytes.Clone();
            PvfDecryptor.Decrypt("HASH", hashBytes); 

            byte[] grpiBytes = (byte[])_rawGrpiBytes.Clone();
            PvfDecryptor.Decrypt("GRPI", grpiBytes);

            
            var header = _header;
            byte[] headerBytes = StructToBytes(header);
            PvfDecryptor.Decrypt("HeaD", headerBytes);
            if (_headerUsesGuard)
                PvfDecryptor.DecryptGuard(headerBytes);

            
            int totalSize = 0x30 + tableBytes.Length + hashBytes.Length +
                            nameBytes.Length + grpiBytes.Length + _bodyLength;
            byte[] result = new byte[totalSize];
            int pos = 0;

            Array.Copy(headerBytes, 0, result, pos, 0x30); pos += 0x30;
            Array.Copy(tableBytes, 0, result, pos, tableBytes.Length); pos += tableBytes.Length;
            Array.Copy(hashBytes, 0, result, pos, hashBytes.Length); pos += hashBytes.Length;
            Array.Copy(nameBytes, 0, result, pos, nameBytes.Length); pos += nameBytes.Length;
            Array.Copy(grpiBytes, 0, result, pos, grpiBytes.Length); pos += grpiBytes.Length;
            CopyBody(0, result, pos, _bodyLength);

            return result;
        }

        private static byte[] StructToBytes<T>(T value) where T : struct
        {
            int size = System.Runtime.InteropServices.Marshal.SizeOf<T>();
            byte[] bytes = new byte[size];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                System.Runtime.InteropServices.Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), false);
            }
            finally
            {
                handle.Free();
            }
            return bytes;
        }

        
        
        
        public static PvfArchive Open(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("PVF 文件不存在", filePath);
            return Open(File.ReadAllBytes(filePath));
        }

        /// <summary>
        /// Open for read-only GM use via mmap: header/table/name/grpi are copied into
        /// managed memory; compressed body stays file-backed and is page-faulted on demand.
        /// Do not use this for pack/save paths that need the full raw layout.
        /// </summary>
        public static PvfArchive OpenReadOnly(string filePath)
        {
            return OpenMapped(filePath);
        }

        /// <summary>
        /// Memory-map Script.pvf so cold open does not allocate a 61MB managed byte[].
        /// When <paramref name="lite"/> is true (default), skips path-index / hash / fileItems array
        /// and resolves paths via <see cref="ExternalPathResolver"/> (disk index).
        /// Pass lite=false for one-shot index rebuild that needs in-memory path lookup.
        /// </summary>
        public static PvfArchive OpenMapped(string filePath, bool lite = true)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("PVF 文件不存在", filePath);

            var mmf = MemoryMappedFile.CreateFromFile(
                filePath,
                FileMode.Open,
                mapName: null,
                capacity: 0,
                MemoryMappedFileAccess.Read);
            MemoryMappedViewAccessor view = null;
            try
            {
                view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                var archive = new PvfArchive();
                archive.ParseMapped(mmf, view, lite);
                return archive;
            }
            catch
            {
                try { view?.Dispose(); } catch { /* ignore */ }
                try { mmf.Dispose(); } catch { /* ignore */ }
                throw;
            }
        }

        /// <summary>
        /// After array parse, retain only the compressed body so the rest of the file buffer
        /// can be collected. No-op for mmap archives.
        /// </summary>
        public void TrimToBodyOnly()
        {
            if (IsMapped)
                return;
            if (_bodyBuffer == null || _bodyLength <= 0)
                return;
            if (_bodyOffset == 0 && _bodyBuffer.Length == _bodyLength)
                return;

            var body = new byte[_bodyLength];
            Buffer.BlockCopy(_bodyBuffer, _bodyOffset, body, 0, _bodyLength);
            _bodyBuffer = body;
            _bodyOffset = 0;
            // keep _rawTableBytes if already materialized; else drop lazy table slice base
            if (_rawTableBytes == null && _rawTableSize > 0)
            {
                // table lived in the discarded prefix; already unavailable — fine for read-only
            }
        }

        
        
        
        public static PvfArchive Open(byte[] data)
        {
            if (data == null || data.Length < 0x30)
                throw new InvalidDataException("数据不足以包含 PVF 头部");

            var archive = new PvfArchive();
            archive.Parse(data);
            return archive;
        }

        
        
        
        public string GetFileContent(PvfFileData file)
        {
            if (file == null) return string.Empty;
            int idx = file.Index >= 0 ? file.Index : _files.IndexOf(file);
            byte[] overlayData;
            if (idx >= 0 && _overlay.TryGetValue(idx, out overlayData))
            {
                
                return DecodeRawData(file.Entry.DataType, overlayData);
            }
            return DecodeFileData(file.Entry);
        }

        
        
        
        public string GetFileContent(int fileIndex)
        {
            if (fileIndex < 0 || fileIndex >= FileCount) return string.Empty;
            byte[] overlayData;
            if (_overlay.TryGetValue(fileIndex, out overlayData))
            {
                var dataType = GetFileEntry(fileIndex).DataType;
                return DecodeRawData(dataType, overlayData);
            }
            return DecodeFileData(GetFileEntry(fileIndex));
        }

        private PvfFileItem GetFileEntry(int fileIndex)
        {
            if (_fileItems != null)
                return _fileItems[fileIndex];
            if (_liteMapped)
                return ReadFileItemFromRawTable(fileIndex);
            return _files[fileIndex].Entry;
        }

        private PvfFileItem ReadFileItemFromRawTable(int fileIndex)
        {
            if (fileIndex < 0 || fileIndex >= _liteFileCount)
                throw new ArgumentOutOfRangeException(nameof(fileIndex));

            // Preferred: managed table copy (rebuild / non-lite).
            if (_rawTableBytes != null)
            {
                var offset = _rawTableOffset + fileIndex * 0x18;
                return new PvfFileItem
                {
                    NameOffset = BitConverter.ToInt32(_rawTableBytes, offset),
                    PathOffset = BitConverter.ToInt32(_rawTableBytes, offset + 4),
                    ChunkIndex = BitConverter.ToInt32(_rawTableBytes, offset + 8),
                    DataOffset = BitConverter.ToInt32(_rawTableBytes, offset + 12),
                    DataSize = BitConverter.ToInt32(_rawTableBytes, offset + 16),
                    DataType = BitConverter.ToInt32(_rawTableBytes, offset + 20),
                };
            }

            // Lite runtime: read 24-byte row straight from mmap — zero managed table allocation.
            if (_mappedView == null || _tableFileOffset <= 0)
                throw new InvalidOperationException("lite mmap 缺少 file table 视图。");
            var pos = _tableFileOffset + (long)fileIndex * 0x18L;
            return new PvfFileItem
            {
                NameOffset = _mappedView.ReadInt32(pos),
                PathOffset = _mappedView.ReadInt32(pos + 4),
                ChunkIndex = _mappedView.ReadInt32(pos + 8),
                DataOffset = _mappedView.ReadInt32(pos + 12),
                DataSize = _mappedView.ReadInt32(pos + 16),
                DataType = _mappedView.ReadInt32(pos + 20),
            };
        }

        
        
        
        public int FindFileIndex(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return -1;

            var normalizedPath = NormalizeArchivePath(relativePath);
            if (_pathIndex.TryGetValue(normalizedPath, out var index))
                return index;

            // Disk path index (schema v3+): O(1) without holding 593k path strings.
            var external = ExternalPathResolver;
            if (external != null)
            {
                var ext = external(normalizedPath);
                if (ext >= 0)
                {
                    RememberStickyPath(normalizedPath, ext);
                    return ext;
                }
            }

            // Compact mode only pre-indexes GM-relevant prefixes; rare paths fall back to a scan.
            if (_compactPathIndex && _fileItems != null)
            {
                var found = ScanFileIndexByPath(normalizedPath);
                if (found >= 0)
                    RememberStickyPath(normalizedPath, found);
                return found;
            }

            return -1;
        }

        private void RememberStickyPath(string normalizedPath, int fileIndex)
        {
            if (_pathIndex.ContainsKey(normalizedPath))
            {
                _pathIndex[normalizedPath] = fileIndex;
                return;
            }
            // Only cap in lite runtime. Rebuild/compact mode owns a large intentional path map.
            if (_liteMapped && _pathIndex.Count >= MaxStickyPathEntries)
            {
                // Dictionary has no cheap LRU order; drop half when full.
                var drop = _pathIndex.Count / 2;
                var keys = new List<string>(drop);
                foreach (var key in _pathIndex.Keys)
                {
                    keys.Add(key);
                    if (keys.Count >= drop)
                        break;
                }
                for (var i = 0; i < keys.Count; i++)
                    _pathIndex.Remove(keys[i]);
            }
            _pathIndex[normalizedPath] = fileIndex;
        }

        /// <summary>
        /// Enumerate GM-relevant archive paths for disk index persistence (rebuild only).
        /// Yields (normalizedPath, fileIndex). Does not materialize the full Files list.
        /// </summary>
        public IEnumerable<KeyValuePair<string, int>> EnumerateRuntimePaths()
        {
            if (_liteMapped)
            {
                // Lite has no in-memory path map; caller should rebuild with lite=false once.
                foreach (var pair in _pathIndex)
                    yield return pair;
                yield break;
            }

            if (_pathIndex.Count > 0)
            {
                foreach (var pair in _pathIndex)
                    yield return pair;
                yield break;
            }

            // Full materialization fallback (pack tools).
            EnsureFilesMaterialized();
            for (var i = 0; i < _files.Count; i++)
            {
                var f = _files[i];
                var p = NormalizeArchivePath(f.Path, f.Name);
                if (!string.IsNullOrEmpty(p))
                    yield return new KeyValuePair<string, int>(p, i);
            }
        }

        
        
        
        public string GetFileContent(string relativePath)
        {
            var fileIndex = FindFileIndex(relativePath);
            return fileIndex >= 0 ? GetFileContent(fileIndex) : string.Empty;
        }

        // Raw bytes are used for byte-level comparison and same-size edit detection.
        public byte[] GetFileRawData(int fileIndex)
        {
            if (fileIndex < 0 || fileIndex >= FileCount) return null;
            return GetFileRawData(GetFileData(fileIndex));
        }

        // Existing paths are replaced; missing paths are appended as new PVF files.
        public void SetFileRawData(string relativePath, byte[] newData, int dataType = 1)
        {
            int fileIndex = FindFileIndex(relativePath);
            if (fileIndex >= 0)
            {
                SetFileRawData(fileIndex, newData);
                return;
            }

            AddFileRawData(relativePath, newData, dataType);
        }

        public void SetFileContent(string relativePath, string text, int dataType = 1)
        {
            int fileIndex = FindFileIndex(relativePath);
            if (fileIndex >= 0)
            {
                SetFileContent(fileIndex, text);
                return;
            }

            AddFileContent(relativePath, text, dataType);
        }

        public int AddFileRawData(string relativePath, byte[] data, int dataType = 1)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("PVF relative path cannot be empty.", nameof(relativePath));

            string normalized = NormalizeArchivePath(relativePath);
            int existingIndex = FindFileIndex(normalized);
            if (existingIndex >= 0)
            {
                SetFileRawData(existingIndex, data);
                return existingIndex;
            }

            SplitArchivePath(normalized, out string path, out string name);
            int nameOffset = GetOrAddStringOffset(name);
            int pathOffset = GetOrAddStringOffset(path ?? string.Empty);

            var item = new PvfFileItem
            {
                NameOffset = nameOffset,
                PathOffset = pathOffset,
                // New files get their real chunk/index offsets during SaveAs().
                ChunkIndex = -1,
                DataOffset = 0,
                DataSize = data != null ? data.Length : 0,
                DataType = dataType
            };

            EnsureFilesMaterialized();
            int index = _files.Count;
            var file = new PvfFileData
            {
                Name = name,
                Path = path ?? string.Empty,
                Entry = item,
                Index = index
            };

            _files.Add(file);
            if (_fileItems != null)
            {
                var expanded = new PvfFileItem[_fileItems.Length + 1];
                Array.Copy(_fileItems, expanded, _fileItems.Length);
                expanded[index] = item;
                _fileItems = expanded;
            }
            _pathIndex[normalized] = index;
            _overlay[index] = data != null ? (byte[])data.Clone() : Array.Empty<byte>();
            _header.FileCount = FileCount;
            return index;
        }

        public int AddFileContent(string relativePath, string text, int dataType = 1)
        {
            byte[] raw = EncodeTextToRaw(dataType, text);
            return AddFileRawData(relativePath, raw, dataType);
        }

        
        
        
        public byte[] GetFileRawData(PvfFileData file)
        {
            if (file == null) return null;
            int idx = file.Index >= 0 ? file.Index : _files.IndexOf(file);
            byte[] overlayData;
            if (idx >= 0 && _overlay.TryGetValue(idx, out overlayData))
                return (byte[])overlayData.Clone();

            var item = file.Entry;
            byte[] chunk = GetChunkData(item.ChunkIndex);
            if (chunk == null || item.DataOffset < 0 || item.DataSize <= 0 ||
                item.DataOffset + item.DataSize > chunk.Length)
                return null;
            return chunk.Slice(item.DataOffset, item.DataSize);
        }

        
        
        
        public void SetFileRawData(int fileIndex, byte[] newData)
        {
            if (fileIndex < 0 || fileIndex >= FileCount)
                throw new ArgumentOutOfRangeException(nameof(fileIndex));
            if (newData == null) newData = Array.Empty<byte>();
            _overlay[fileIndex] = newData;
        }

        
        
        
        public void SetFileContent(int fileIndex, string text)
        {
            if (fileIndex < 0 || fileIndex >= FileCount)
                throw new ArgumentOutOfRangeException(nameof(fileIndex));
            var item = GetFileEntry(fileIndex);
            byte[] encoded = EncodeTextToRaw(item.DataType, text);
            _overlay[fileIndex] = encoded;
        }

        
        
        
        public bool IsFileModified(int fileIndex)
        {
            return _overlay.ContainsKey(fileIndex);
        }

        
        
        
        public void RevertFile(int fileIndex)
        {
            _overlay.Remove(fileIndex);
        }

        
        
        
        public void RevertAll()
        {
            _overlay.Clear();
        }

        
        
        
        public int IndexOf(PvfFileData file)
        {
            if (file == null) return -1;
            return file.Index >= 0 ? file.Index : _files.IndexOf(file);
        }

        
        
        
        public byte[] GetChunkData(int chunkIndex)
        {
            if (chunkIndex < 0 || chunkIndex >= _groups.Count)
                return null;

            return _chunkCache.GetOrAdd(chunkIndex, LoadChunkUncached);
        }

        /// <summary>Drop decompressed chunk pages after bulk indexing to release RSS.</summary>
        public void ClearChunkCache()
        {
            _chunkCache.Clear();
        }

        private byte[] LoadChunkUncached(int ci)
        {
            var prev = ci > 0 ? _groups[ci - 1] : default;
            var curr = _groups[ci];

            int relative = prev.CompressedSize;
            int size = curr.CompressedSize - prev.CompressedSize;
            if (size <= 0 || relative < 0 || relative + size > _bodyLength)
                return null;

            byte[] encrypted = new byte[size];
            CopyBody(relative, encrypted, 0, size);
            PvfDecryptor.Decrypt("BodY", encrypted);
            return PvfDecryptor.ZlibDecompress(encrypted);
        }

        
        
        
        public byte[] GetChunkRawEncrypted(int chunkIndex)
        {
            if (chunkIndex < 0 || chunkIndex >= _groups.Count)
                return null;

            var prev = chunkIndex > 0 ? _groups[chunkIndex - 1] : default;
            var curr = _groups[chunkIndex];

            int relative = prev.CompressedSize;
            int size = curr.CompressedSize - prev.CompressedSize;
            if (size <= 0 || relative < 0 || relative + size > _bodyLength)
                return null;

            var result = new byte[size];
            CopyBody(relative, result, 0, size);
            return result;
        }

        private void CopyBody(int bodyRelativeOffset, byte[] dest, int destOffset, int count)
        {
            if (count <= 0)
                return;
            if (bodyRelativeOffset < 0 || count < 0 || bodyRelativeOffset + count > _bodyLength)
                throw new ArgumentOutOfRangeException(nameof(bodyRelativeOffset));

            if (IsMapped)
            {
                _mappedView.ReadArray(_bodyFileOffset + bodyRelativeOffset, dest, destOffset, count);
                return;
            }

            if (_bodyBuffer == null)
                throw new InvalidOperationException("PVF body 未加载。");
            Buffer.BlockCopy(_bodyBuffer, _bodyOffset + bodyRelativeOffset, dest, destOffset, count);
        }

        #region 解析流程

        private void ParseMapped(MemoryMappedFile mmf, MemoryMappedViewAccessor view, bool lite)
        {
            _mappedFile = mmf;
            _mappedView = view;
            _liteMapped = lite;

            byte[] rawHeaderBytes = new byte[0x30];
            view.ReadArray(0, rawHeaderBytes, 0, 0x30);
            var header = DecodeHeaderWithFallback(rawHeaderBytes, view.Capacity);
            _header = header;
            _liteFileCount = header.FileCount;

            int pos = 0x30;
            int tableOffset = pos;
            int tableSize = header.FileCount * 0x18;
            pos += tableSize;

            int hashOffset = pos;
            pos += header.HashTableSize;

            int nameOffset = pos;
            pos += header.NameTableSize;

            int grpiOffset = pos;
            int grpiSize = header.GroupCount * 8;
            pos += grpiSize;

            long fileLength = view.Capacity;
            if (pos + header.BodySize > fileLength)
                throw new InvalidDataException("PVF body 超出文件长度");

            // Body stays mapped — no 61MB managed allocation.
            _bodyBuffer = null;
            _bodyOffset = 0;
            _bodyLength = header.BodySize;
            _bodyFileOffset = pos;
            _rawTableSize = tableSize;

            // Name table needed to decode script string tokens inside file bodies (~6MB).
            byte[] nameBytes = new byte[header.NameTableSize];
            view.ReadArray(nameOffset, nameBytes, 0, header.NameTableSize);
            // Keep a single buffer: BuildStringBuffers decrypts in place; no clone.
            _rawNameBytes = nameBytes;

            byte[] grpiBytes = new byte[grpiSize];
            view.ReadArray(grpiOffset, grpiBytes, 0, grpiSize);
            PvfDecryptor.Decrypt("GRPI", grpiBytes);
            _rawGrpiBytes = grpiBytes;

            BuildStringBuffers(nameBytes);
            ParseGroupItemsFast(header.GroupCount, grpiBytes);

            if (lite)
            {
                // Runtime path: no 14MB managed file table, no 593k path strings, no hash/fileItems.
                // File rows are read from mmap on demand; path → index via ExternalPathResolver.
                _rawTableBytes = null;
                _rawTableOffset = 0;
                _tableFileOffset = tableOffset;
                _fileItems = null;
                _filesMaterialized = true; // empty _files; GetFileEntry uses mmap table rows
                _compactPathIndex = false;
                _hashTable = null;
                _rawHashBytes = null;
                return;
            }

            // Rebuild path: need managed table + prefix path index for bulk GetFileContent.
            _rawTableBytes = new byte[tableSize];
            view.ReadArray(tableOffset, _rawTableBytes, 0, tableSize);
            _rawTableOffset = 0;
            _tableFileOffset = 0;

            byte[] hashBytes = new byte[header.HashTableSize];
            view.ReadArray(hashOffset, hashBytes, 0, header.HashTableSize);
            PvfDecryptor.Decrypt("HASH", hashBytes);
            _rawHashBytes = hashBytes;
            ParseFileItemsCompact(header.FileCount, _rawTableBytes, 0, prefixPathIndexOnly: true);
            _hashTable = PvfHashTable.Parse(hashBytes);
        }

        private void Parse(byte[] allBytes)
        {
            byte[] rawHeaderBytes = allBytes.Slice(0, 0x30);
            var header = DecodeHeaderWithFallback(rawHeaderBytes, allBytes.Length);
            _header = header;

            
            int pos = 0x30;
            int tableOffset = pos;
            int tableSize = header.FileCount * 0x18;
            pos += tableSize;

            int hashOffset = pos;
            pos += header.HashTableSize;

            int nameOffset = pos;
            pos += header.NameTableSize;

            int grpiOffset = pos;
            int grpiSize = header.GroupCount * 8;
            pos += grpiSize;

            
            _bodyBuffer = allBytes;
            _bodyOffset = pos;
            _bodyLength = header.BodySize;

            
            
            _rawTableOffset = tableOffset;
            _rawTableSize = tableSize;

            
            byte[] hashBytes = allBytes.Slice(hashOffset, header.HashTableSize);
            byte[] nameBytes = allBytes.Slice(nameOffset, header.NameTableSize);
            byte[] grpiBytes = allBytes.Slice(grpiOffset, grpiSize);

            _rawNameBytes = (byte[])nameBytes.Clone(); 

            
            PvfDecryptor.Decrypt("GRPI", grpiBytes);
            PvfDecryptor.Decrypt("HASH", hashBytes);
            _rawGrpiBytes = grpiBytes;
            _rawHashBytes = hashBytes;

            BuildStringBuffers(nameBytes);

            
            ParseFileItemsFast(header.FileCount, allBytes, tableOffset);
            ParseGroupItemsFast(header.GroupCount, grpiBytes);
            _hashTable = PvfHashTable.Parse(hashBytes);
        }

        private PvfHeader DecodeHeaderWithFallback(byte[] rawHeaderBytes, long dataLength)
        {
            PvfHeader header = default;
            bool decoded = false;
            Exception lastHeaderError = null;
            foreach (var usesGuard in new[] { true, false })
            {
                try
                {
                    header = DecodeHeaderCandidate(rawHeaderBytes, usesGuard, dataLength);
                    _headerUsesGuard = usesGuard;
                    decoded = true;
                    break;
                }
                catch (InvalidDataException ex)
                {
                    lastHeaderError = ex;
                }
            }

            if (!decoded)
                throw new InvalidDataException("PVF 头部无法匹配已支持的格式", lastHeaderError);
            return header;
        }

        private static PvfHeader DecodeHeaderCandidate(byte[] rawHeaderBytes, bool usesGuard, long dataLength)
        {
            byte[] headerBytes = (byte[])rawHeaderBytes.Clone();
            if (usesGuard)
                PvfDecryptor.DecryptGuard(headerBytes);
            if (PvfDecryptor.Decrypt("HeaD", headerBytes) != 0)
                throw new InvalidDataException("PVF 头部解密失败");

            var header = headerBytes.ToStruct<PvfHeader>();
            if (header.Signature != MagicSignature)
                throw new InvalidDataException("无效的 PVF 签名");
            ValidateHeaderLayout(header, dataLength);
            return header;
        }

        private static void ValidateHeaderLayout(PvfHeader header, long dataLength)
        {
            if (header.FileCount < 0 || header.GroupCount < 0 ||
                header.HashTableSize < 0 || header.NameTableSize < 0 || header.BodySize < 0)
            {
                throw new InvalidDataException("PVF 头部包含负的区段尺寸");
            }

            try
            {
                long declaredLength = checked(
                    0x30L + (long)header.FileCount * 0x18 + header.HashTableSize +
                    header.NameTableSize + (long)header.GroupCount * 8 + header.BodySize);
                if (declaredLength > dataLength)
                    throw new InvalidDataException("PVF 头部区段超出文件边界");
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException("PVF 头部区段尺寸溢出", ex);
            }
        }

        private void BuildStringBuffers(byte[] nameBytes)
        {
            if (nameBytes == null || nameBytes.Length < 16) return;

            int idx = 8; 
            _strABuffer = DecryptStringBuffer(nameBytes, ref idx, "sTrA", 0xAA74472E);
            _strWBuffer = DecryptStringBuffer(nameBytes, ref idx, "sTrW", 0x9A82F037);
        }

        private static byte[] DecryptStringBuffer(byte[] bytes, ref int index, string key, uint xorConst)
        {
            if (index + 8 > bytes.Length)
                return Array.Empty<byte>();

            int cnt1 = BitConverter.ToInt32(bytes, index); index += 4;
            int cnt2 = BitConverter.ToInt32(bytes, index); index += 4;

            int encSize = (int)(cnt1 ^ xorConst);
            if (encSize <= 0 || index + encSize > bytes.Length)
                return Array.Empty<byte>();

            byte[] encrypted = bytes.Slice(index, encSize);
            index += encSize;

            PvfDecryptor.Decrypt2(key, encrypted);
            return PvfDecryptor.ZlibDecompress(encrypted);
        }

        private void ParseFileItemsFast(int count, byte[] buffer, int offset)
        {
            ParseFileItemsCompact(count, buffer, offset, prefixPathIndexOnly: false);
            EnsureFilesMaterialized();
        }

        private void ParseFileItemsCompact(int count, byte[] buffer, int offset, bool prefixPathIndexOnly)
        {
            _fileItems = new PvfFileItem[count];
            _filesMaterialized = false;
            _compactPathIndex = prefixPathIndexOnly;
            _files.Clear();
            _pathIndex.Clear();

            // Prefix mode only keeps GM trees; full mode indexes everything for pack/edit tools.
            // Avoid retaining name/path strings for the ~400k+ non-GM files.
            var stringCache = new Dictionary<int, string>(
                prefixPathIndexOnly ? Math.Max(16, count / 16) : Math.Max(16, count / 4));
            unsafe
            {
                fixed (byte* pBase = buffer)
                {
                    byte* pTable = pBase + offset;
                    for (int i = 0; i < count; i++)
                    {
                        PvfFileItem* pItem = (PvfFileItem*)(pTable + i * 0x18);
                        var item = *pItem;
                        _fileItems[i] = item;

                        if (!stringCache.TryGetValue(item.PathOffset, out string path))
                        {
                            path = ResolveString(item.PathOffset);
                            // Only retain path strings we will index; 593k temp strings otherwise pin LOH.
                            if (!prefixPathIndexOnly || IsRuntimePathCandidate(path))
                                stringCache[item.PathOffset] = path;
                        }

                        if (prefixPathIndexOnly && !IsRuntimePathCandidate(path))
                        {
                            // Directory alone excludes most non-GM trees (sprites, sound, ...).
                            // Only empty path needs the file name to decide.
                            if (!string.IsNullOrEmpty(path))
                                continue;
                        }

                        if (!stringCache.TryGetValue(item.NameOffset, out string name))
                        {
                            name = ResolveString(item.NameOffset);
                            stringCache[item.NameOffset] = name;
                        }

                        if (prefixPathIndexOnly && string.IsNullOrEmpty(path) && !IsRuntimeIndexedPath(name))
                            continue;

                        var archivePath = NormalizeArchivePath(path, name);
                        if (prefixPathIndexOnly && !IsRuntimeIndexedPath(archivePath))
                            continue;
                        if (!_pathIndex.ContainsKey(archivePath))
                            _pathIndex.Add(archivePath, i);
                    }
                }
            }
        }

        private static bool IsRuntimePathCandidate(string path)
        {
            if (string.IsNullOrEmpty(path))
                return true; // decide with file name
            var normalized = path.Replace('\\', '/').Trim().TrimEnd('/');
            if (normalized.Length == 0)
                return true;
            // "equipment", "equipment/weapon", "skill/swordman"
            for (var i = 0; i < RuntimePathPrefixes.Length; i++)
            {
                var prefix = RuntimePathPrefixes[i]; // ends with '/'
                if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
                // directory root equal to prefix without slash
                if (normalized.Length == prefix.Length - 1
                    && normalized.Equals(prefix.Substring(0, prefix.Length - 1), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsRuntimeIndexedPath(string archivePath)
        {
            if (string.IsNullOrEmpty(archivePath))
                return false;
            // Bare filenames used by a few etc lookups.
            if (archivePath.IndexOf('/') < 0)
                return true;
            for (var i = 0; i < RuntimePathPrefixes.Length; i++)
            {
                if (archivePath.StartsWith(RuntimePathPrefixes[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private int ScanFileIndexByPath(string normalizedPath)
        {
            if (_fileItems == null || _fileItems.Length == 0)
                return -1;
            for (var i = 0; i < _fileItems.Length; i++)
            {
                var item = _fileItems[i];
                var name = ResolveString(item.NameOffset);
                var path = ResolveString(item.PathOffset);
                if (string.Equals(NormalizeArchivePath(path, name), normalizedPath, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private void EnsureFilesMaterialized()
        {
            if (_filesMaterialized)
                return;
            if (_fileItems == null)
            {
                _filesMaterialized = true;
                return;
            }

            _files.Clear();
            _files.Capacity = _fileItems.Length;
            var stringCache = new Dictionary<int, string>(_fileItems.Length / 4);
            for (var i = 0; i < _fileItems.Length; i++)
            {
                var item = _fileItems[i];
                if (!stringCache.TryGetValue(item.NameOffset, out var name))
                {
                    name = ResolveString(item.NameOffset);
                    stringCache[item.NameOffset] = name;
                }
                if (!stringCache.TryGetValue(item.PathOffset, out var path))
                {
                    path = ResolveString(item.PathOffset);
                    stringCache[item.PathOffset] = path;
                }
                _files.Add(new PvfFileData
                {
                    Name = name,
                    Path = path,
                    Entry = item,
                    Index = i
                });
            }
            _filesMaterialized = true;
        }

        private PvfFileData GetFileData(int fileIndex)
        {
            if (fileIndex < 0)
                return null;
            if (_filesMaterialized)
            {
                if (fileIndex >= _files.Count)
                    return null;
                return _files[fileIndex];
            }
            if (_fileItems == null || fileIndex >= _fileItems.Length)
                return null;

            var item = _fileItems[fileIndex];
            return new PvfFileData
            {
                Name = ResolveString(item.NameOffset),
                Path = ResolveString(item.PathOffset),
                Entry = item,
                Index = fileIndex
            };
        }

        private static string NormalizeArchivePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return string.Empty;

            var normalized = relativePath.Replace('\\', '/').Trim();
            while (normalized.StartsWith("./", StringComparison.Ordinal) || normalized.StartsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.StartsWith("./", StringComparison.Ordinal)
                    ? normalized.Substring(2)
                    : normalized.Substring(1);
            }

            return normalized.TrimEnd('/');
        }

        private static string NormalizeArchivePath(string path, string name)
        {
            if (string.IsNullOrEmpty(path))
                return NormalizeArchivePath(name);

            if (string.IsNullOrEmpty(name))
                return NormalizeArchivePath(path);

            return NormalizeArchivePath(path + "/" + name);
        }

        private unsafe void ParseGroupItemsFast(int count, byte[] grpiBytes)
        {
            _groups.Capacity = count;
            fixed (byte* pBase = grpiBytes)
            {
                for (int i = 0; i < count; i++)
                {
                    GrpiItem* pItem = (GrpiItem*)(pBase + i * 8);
                    _groups.Add(*pItem);
                }
            }
        }

        #endregion

        #region 文件内容解码

        private string DecodeFileData(PvfFileItem item)
        {
            switch (item.DataType)
            {
                case 1: return DecodeType1(item);  
                case 3: return DecodeType3(item);  
                default: return string.Empty;
            }
        }

        
        
        
        private string DecodeRawData(int dataType, byte[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;
            switch (dataType)
            {
                case 1: return DecodeType1Raw(data);
                case 3: return Encoding.Unicode.GetString(data);
                default: return string.Empty;
            }
        }

        
        
        
        // Re-encode decompiled text back to the raw PVF payload format.
        internal byte[] EncodeTextToRaw(int dataType, string text)
        {
            if (text == null) text = string.Empty;
            switch (dataType)
            {
                case 1: return EncodeType1Text(text);
                case 3: return Encoding.Unicode.GetBytes(text);
                default: return Encoding.UTF8.GetBytes(text);
            }
        }

        
        
        
        private string DecodeType1(PvfFileItem item)
        {
            byte[] chunk = GetChunkData(item.ChunkIndex);
            if (chunk == null || item.DataOffset < 0 || item.DataSize <= 0 ||
                item.DataOffset + item.DataSize > chunk.Length)
                return string.Empty;

            byte[] data = chunk.Slice(item.DataOffset, item.DataSize);
            return DecodeType1Raw(data);
        }

        
        
        
        private string DecodeType1Raw(byte[] data)
        {
            int lineCount = data.Length / 5;
            if (lineCount == 0) return string.Empty;

            var sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < lineCount; i++)
            {
                int off = i * 5;
                byte type = data[off];
                int value = BitConverter.ToInt32(data, off + 1);
                AppendScriptToken(sb, type, value);
            }
            return sb.ToString();
        }

        private void AppendScriptToken(StringBuilder sb, byte type, int value)
        {
            switch (type)
            {
                case 0: 
                    sb.Append(value).Append(' ');
                    break;
                case 2: 
                    sb.Append(BitConverter.ToSingle(BitConverter.GetBytes(value), 0).ToString("R", CultureInfo.InvariantCulture)).Append(' ');
                    break;
                case 3: 
                    sb.AppendLine().Append(ResolveString(value)).AppendLine();
                    break;
                case 5: 
                    sb.AppendLine().Append("{5=`").Append(EscapeBacktickString(ResolveString(value))).Append("`}");
                    break;
                case 6: 
                    sb.Append('`').Append(EscapeBacktickString(ResolveString(value))).Append("` ");
                    break;
                case 7: 
                    sb.AppendLine().Append("{7=`").Append(EscapeBacktickString(ResolveString(value))).Append("`}");
                    break;
            }
        }

        private static string EscapeBacktickString(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Replace("`", "``");
        }

        private byte[] EncodeType1Text(string text)
        {
            var tokens = new List<Type1Token>();
            int i = 0;
            while (i < text.Length)
            {
                char ch = text[i];
                if (char.IsWhiteSpace(ch))
                {
                    i++;
                    continue;
                }

                if (ch == '#')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }

                if (ch == '`')
                {
                    string value;
                    int nextIndex;
                    if (TryReadBacktickString(text, i, out value, out nextIndex))
                    {
                        tokens.Add(new Type1Token(6, GetOrAddStringOffset(value)));
                        i = nextIndex;
                        continue;
                    }
                }

                if (ch == '{')
                {
                    int end = FindMarkerEnd(text, i + 1);
                    if (end > i)
                    {
                        string marker = text.Substring(i, end - i + 1).Trim();
                        Type1Token markerToken;
                        if (TryParseSpecialMarker(marker, out markerToken))
                        {
                            tokens.Add(markerToken);
                            i = end + 1;
                            continue;
                        }
                    }
                }

                if (ch == '[')
                {
                    int end = text.IndexOf(']', i + 1);
                    if (end > i)
                    {
                        string tag = text.Substring(i, end - i + 1);
                        tokens.Add(new Type1Token(3, GetOrAddStringOffset(tag)));
                        i = end + 1;
                        continue;
                    }
                }

                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    if (text[i] == '`' || text[i] == '{' || text[i] == '[')
                        break;
                    i++;
                }

                if (i == start)
                {
                    i++;
                    continue;
                }

                string token = text.Substring(start, i - start);
                int intValue;
                float floatValue;
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
                {
                    tokens.Add(new Type1Token(0, intValue));
                }
                else if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out floatValue))
                {
                    tokens.Add(new Type1Token(2, BitConverter.ToInt32(BitConverter.GetBytes(floatValue), 0)));
                }
                else
                {
                    tokens.Add(new Type1Token(3, GetOrAddStringOffset(token)));
                }
            }

            byte[] raw = new byte[tokens.Count * 5];
            for (int n = 0; n < tokens.Count; n++)
            {
                int off = n * 5;
                raw[off] = tokens[n].Type;
                byte[] valueBytes = BitConverter.GetBytes(tokens[n].Value);
                Buffer.BlockCopy(valueBytes, 0, raw, off + 1, 4);
            }
            return raw;
        }

        private static bool TryReadBacktickString(string text, int start, out string value, out int nextIndex)
        {
            value = string.Empty;
            nextIndex = start + 1;
            if (start < 0 || start >= text.Length || text[start] != '`')
                return false;

            var sb = new StringBuilder();
            int i = start + 1;
            while (i < text.Length)
            {
                if (text[i] == '`')
                {
                    if (i + 1 < text.Length && text[i + 1] == '`')
                    {
                        sb.Append('`');
                        i += 2;
                        continue;
                    }

                    value = sb.ToString();
                    nextIndex = i + 1;
                    return true;
                }

                sb.Append(text[i]);
                i++;
            }

            value = sb.ToString();
            nextIndex = i;
            return true;
        }

        private static int FindMarkerEnd(string text, int start)
        {
            bool inBacktick = false;
            for (int i = start; i < text.Length; i++)
            {
                if (text[i] == '`')
                {
                    if (inBacktick && i + 1 < text.Length && text[i + 1] == '`')
                    {
                        i++;
                        continue;
                    }
                    inBacktick = !inBacktick;
                    continue;
                }

                if (!inBacktick && text[i] == '}')
                    return i;
            }
            return -1;
        }

        private bool TryParseSpecialMarker(string marker, out Type1Token token)
        {
            token = default(Type1Token);
            if (string.IsNullOrEmpty(marker) || marker.Length < 4 || marker[0] != '{' || marker[marker.Length - 1] != '}')
                return false;

            byte type;
            if (marker.StartsWith("{5=", StringComparison.OrdinalIgnoreCase))
                type = 5;
            else if (marker.StartsWith("{7=", StringComparison.OrdinalIgnoreCase))
                type = 7;
            else
                return false;

            string inner = marker.Substring(3, marker.Length - 4).Trim();
            if (inner.Length >= 2 && inner[0] == '`' && inner[inner.Length - 1] == '`')
                inner = inner.Substring(1, inner.Length - 2);

            int numericValue;
            int value = int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out numericValue)
                ? numericValue
                : GetOrAddStringOffset(inner);
            token = new Type1Token(type, value);
            return true;
        }

        private struct Type1Token
        {
            public readonly byte Type;
            public readonly int Value;

            public Type1Token(byte type, int value)
            {
                Type = type;
                Value = value;
            }
        }

        
        
        
        private string DecodeType3(PvfFileItem item)
        {
            byte[] chunk = GetChunkData(item.ChunkIndex);
            if (chunk == null || item.DataOffset < 0 || item.DataSize <= 0 ||
                item.DataOffset + item.DataSize > chunk.Length)
                return string.Empty;

            return Encoding.Unicode.GetString(chunk, item.DataOffset, item.DataSize);
        }

        #endregion

        #region 字符串表

        
        
        
        
        public string ResolveString(int magicOffset)
        {
            if (magicOffset < 0) return string.Empty;

            if ((magicOffset & 1) != 0) 
            {
                int offset = (magicOffset >> 1) * 2;
                return ReadUnicodeString(_strWBuffer, offset);
            }
            else 
            {
                int offset = magicOffset >> 1;
                return ReadUtf8String(_strABuffer, offset);
            }
        }

        private static string ReadUtf8String(byte[] buffer, int start)
        {
            if (buffer == null || start < 0 || start >= buffer.Length)
                return string.Empty;

            int end = Array.IndexOf(buffer, (byte)0, start);
            if (end < start) return string.Empty;
            return Encoding.UTF8.GetString(buffer, start, end - start);
        }

        private static string ReadUnicodeString(byte[] buffer, int start)
        {
            if (buffer == null || start < 0 || start >= buffer.Length)
                return string.Empty;

            for (int i = start; i < buffer.Length - 1; i += 2)
            {
                if (buffer[i] == 0 && buffer[i + 1] == 0)
                {
                    int len = i - start;
                    if (len <= 0) return string.Empty;
                    len = (len / 2) * 2; 
                    return Encoding.Unicode.GetString(buffer, start, len);
                }
            }

            
            int remaining = ((buffer.Length - start) / 2) * 2;
            return remaining > 0
                ? Encoding.Unicode.GetString(buffer, start, remaining)
                : string.Empty;
        }

        internal int GetOrAddStringOffset(string value, bool preferUnicode = false)
        {
            if (value == null) value = string.Empty;
            EnsureStringOffsetCache();

            int offset;
            if (!preferUnicode && _strAOffsetCache.TryGetValue(value, out offset))
                return offset;
            if (_strWOffsetCache.TryGetValue(value, out offset))
                return offset;
            if (preferUnicode && _strAOffsetCache.TryGetValue(value, out offset))
                return offset;

            if (preferUnicode)
                return AppendUnicodeString(value);
            return AppendUtf8String(value);
        }

        // Repack sTrA/sTrW so added names and script strings resolve in the new PVF.
        internal byte[] BuildNameTableBytes()
        {
            byte[] strA = _strABuffer ?? new byte[] { 0 };
            byte[] strW = _strWBuffer ?? new byte[] { 0, 0 };

            using (var ms = new MemoryStream())
            {
                if (_rawNameBytes != null && _rawNameBytes.Length >= 8)
                    ms.Write(_rawNameBytes, 0, 8);
                else
                    ms.Write(new byte[8], 0, 8);

                WriteNameTableSection(ms, "sTrA", strA, 0xAA74472E);
                WriteNameTableSection(ms, "sTrW", strW, 0x9A82F037);
                return ms.ToArray();
            }
        }

        private void EnsureStringOffsetCache()
        {
            if (_strAOffsetCache != null && _strWOffsetCache != null)
                return;

            _strAOffsetCache = new Dictionary<string, int>(StringComparer.Ordinal);
            _strWOffsetCache = new Dictionary<string, int>(StringComparer.Ordinal);

            if (_strABuffer == null) _strABuffer = new byte[] { 0 };
            if (_strWBuffer == null) _strWBuffer = new byte[] { 0, 0 };

            int pos = 0;
            while (pos < _strABuffer.Length)
            {
                int end = Array.IndexOf(_strABuffer, (byte)0, pos);
                if (end < 0) end = _strABuffer.Length;
                string value = end > pos ? Encoding.UTF8.GetString(_strABuffer, pos, end - pos) : string.Empty;
                if (!_strAOffsetCache.ContainsKey(value))
                    _strAOffsetCache[value] = pos << 1;
                pos = end + 1;
            }

            pos = 0;
            while (pos + 1 < _strWBuffer.Length)
            {
                int end = pos;
                while (end + 1 < _strWBuffer.Length && !(_strWBuffer[end] == 0 && _strWBuffer[end + 1] == 0))
                    end += 2;
                string value = end > pos ? Encoding.Unicode.GetString(_strWBuffer, pos, end - pos) : string.Empty;
                if (!_strWOffsetCache.ContainsKey(value))
                    _strWOffsetCache[value] = ((pos / 2) << 1) | 1;
                pos = end + 2;
            }
        }

        private int AppendUtf8String(string value)
        {
            byte[] textBytes = Encoding.UTF8.GetBytes(value);
            int oldLength = _strABuffer != null ? _strABuffer.Length : 0;
            byte[] next = new byte[oldLength + textBytes.Length + 1];
            if (_strABuffer != null) Buffer.BlockCopy(_strABuffer, 0, next, 0, _strABuffer.Length);
            Buffer.BlockCopy(textBytes, 0, next, oldLength, textBytes.Length);
            next[next.Length - 1] = 0;
            _strABuffer = next;

            int magicOffset = oldLength << 1;
            _strAOffsetCache[value] = magicOffset;
            return magicOffset;
        }

        private int AppendUnicodeString(string value)
        {
            byte[] textBytes = Encoding.Unicode.GetBytes(value);
            int oldLength = _strWBuffer != null ? _strWBuffer.Length : 0;
            if ((oldLength & 1) != 0) oldLength++;

            byte[] next = new byte[oldLength + textBytes.Length + 2];
            if (_strWBuffer != null) Buffer.BlockCopy(_strWBuffer, 0, next, 0, _strWBuffer.Length);
            Buffer.BlockCopy(textBytes, 0, next, oldLength, textBytes.Length);
            _strWBuffer = next;

            int magicOffset = ((oldLength / 2) << 1) | 1;
            _strWOffsetCache[value] = magicOffset;
            return magicOffset;
        }

        private static void WriteNameTableSection(Stream output, string key, byte[] rawBuffer, uint xorConst)
        {
            if (rawBuffer == null || rawBuffer.Length == 0)
                rawBuffer = key == "sTrW" ? new byte[] { 0, 0 } : new byte[] { 0 };

            byte[] compressed = PvfDecryptor.ZlibCompress(rawBuffer);
            byte[] encrypted = (byte[])compressed.Clone();
            PvfDecryptor.Decrypt2(key, encrypted);

            WriteInt32(output, (int)(encrypted.Length ^ xorConst));
            WriteInt32(output, rawBuffer.Length ^ encrypted.Length);
            output.Write(encrypted, 0, encrypted.Length);
        }

        private static void WriteInt32(Stream output, int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            output.Write(bytes, 0, bytes.Length);
        }

        private static void SplitArchivePath(string relativePath, out string path, out string name)
        {
            string normalized = NormalizeArchivePath(relativePath);
            int slash = normalized.LastIndexOf('/');
            if (slash < 0)
            {
                path = string.Empty;
                name = normalized;
                return;
            }

            path = normalized.Substring(0, slash);
            name = normalized.Substring(slash + 1);
        }

        #endregion

        
        
        
        public PvfHashTable RebuildHashTable()
        {
            var items = new PvfFileItem[_files.Count];
            for (int i = 0; i < _files.Count; i++)
                items[i] = _files[i].Entry;
            return PvfHashTable.Build(items, ResolveString);
        }

        
        
        
        
        // Save writes a new PVF container, but only rebuilds chunks touched by overlay.
        public void SaveAs(string outputPath, Action<int, int> onProgress = null)
        {
            if (_overlay.Count == 0 && _files.Count == _header.FileCount)
            {
                File.WriteAllBytes(outputPath, ToBytes());
                return;
            }

            var modifiedChunks = new HashSet<int>();
            var newFileIndices = new List<int>();
            for (int i = 0; i < _files.Count; i++)
            {
                var item = _files[i].Entry;
                if (item.ChunkIndex < 0 || item.ChunkIndex >= _groups.Count)
                {
                    newFileIndices.Add(i);
                    continue;
                }

                if (_overlay.ContainsKey(i))
                    modifiedChunks.Add(item.ChunkIndex);
            }

            int originalChunkCount = _groups.Count;

            string outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            string tempBodyPath = outputPath + ".body.tmp";
            var newGroups = new List<GrpiItem>(originalChunkCount + (newFileIndices.Count > 0 ? 1 : 0));
            var newItems = new PvfFileItem[_files.Count];
            for (int i = 0; i < _files.Count; i++)
                newItems[i] = _files[i].Entry;

            int cumulativeCompressed = 0;

            try
            {
                using (var bodyStream = new FileStream(tempBodyPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024))
                {
                    for (int ci = 0; ci < originalChunkCount; ci++)
                    {
                        if (!modifiedChunks.Contains(ci))
                        {
                            byte[] rawEncrypted = GetChunkRawEncrypted(ci);
                            if (rawEncrypted != null)
                            {
                                bodyStream.Write(rawEncrypted, 0, rawEncrypted.Length);
                                cumulativeCompressed += rawEncrypted.Length;
                                newGroups.Add(new GrpiItem
                                {
                                    CompressedSize = cumulativeCompressed,
                                    OriginalSize = _groups[ci].OriginalSize
                                });
                            }
                        }
                        else
                        {
                            byte[] originalChunk = GetChunkData(ci);
                            byte[] newChunk = RebuildChunkWithOverlay(ci, originalChunk, newItems);

                            byte[] compressed = PvfDecryptor.ZlibCompress(newChunk);
                            byte[] encrypted = (byte[])compressed.Clone();
                            PvfDecryptor.Decrypt("BodY", encrypted);

                            bodyStream.Write(encrypted, 0, encrypted.Length);
                            cumulativeCompressed += encrypted.Length;
                            newGroups.Add(new GrpiItem
                            {
                                CompressedSize = cumulativeCompressed,
                                OriginalSize = newChunk.Length
                            });
                        }

                        if (onProgress != null && (ci % 100 == 0 || ci == originalChunkCount - 1))
                            onProgress(ci + 1, originalChunkCount + (newFileIndices.Count > 0 ? 1 : 0));
                    }

                    if (newFileIndices.Count > 0)
                    {
                        int newChunkIndex = newGroups.Count;
                        using (var chunkStream = new MemoryStream())
                        {
                            foreach (int fileIndex in newFileIndices)
                            {
                                byte[] data;
                                if (!_overlay.TryGetValue(fileIndex, out data) || data == null)
                                    data = Array.Empty<byte>();

                                var item = newItems[fileIndex];
                                item.ChunkIndex = newChunkIndex;
                                item.DataOffset = (int)chunkStream.Position;
                                item.DataSize = data.Length;
                                if (data.Length > 0)
                                    chunkStream.Write(data, 0, data.Length);
                                newItems[fileIndex] = item;
                            }

                            byte[] newChunk = chunkStream.ToArray();
                            if (newChunk.Length > 0)
                            {
                                byte[] compressed = PvfDecryptor.ZlibCompress(newChunk);
                                byte[] encrypted = (byte[])compressed.Clone();
                                PvfDecryptor.Decrypt("BodY", encrypted);

                                bodyStream.Write(encrypted, 0, encrypted.Length);
                                cumulativeCompressed += encrypted.Length;
                                newGroups.Add(new GrpiItem
                                {
                                    CompressedSize = cumulativeCompressed,
                                    OriginalSize = newChunk.Length
                                });
                            }
                            else if (newChunkIndex > 0)
                            {
                                foreach (int fileIndex in newFileIndices)
                                {
                                    var item = newItems[fileIndex];
                                    item.ChunkIndex = newChunkIndex - 1;
                                    item.DataOffset = 0;
                                    item.DataSize = 0;
                                    newItems[fileIndex] = item;
                                }
                            }
                        }

                        if (onProgress != null)
                            onProgress(originalChunkCount + 1, originalChunkCount + 1);
                    }
                }

                byte[] tableBytes = new byte[newItems.Length * 0x18];
                for (int i = 0; i < newItems.Length; i++)
                {
                    byte[] itemBytes = StructToBytes(newItems[i]);
                    Array.Copy(itemBytes, 0, tableBytes, i * 0x18, 0x18);
                }

                // HASH must be rebuilt when file paths/counts or offsets change.
                byte[] hashBytes = PvfHashTable.Build(newItems, ResolveString).ToBytes();
                PvfDecryptor.Decrypt("HASH", hashBytes);
                byte[] nameBytes = BuildNameTableBytes();

                byte[] grpiBytes = new byte[newGroups.Count * 8];
                for (int i = 0; i < newGroups.Count; i++)
                {
                    byte[] g = StructToBytes(newGroups[i]);
                    Array.Copy(g, 0, grpiBytes, i * 8, 8);
                }
                PvfDecryptor.Decrypt("GRPI", grpiBytes);

                var header = _header;
                // Header sizes must match the rebuilt table/name/grpi/body sections.
                header.FileCount = newItems.Length;
                header.BodySize = cumulativeCompressed;
                header.GroupCount = newGroups.Count;
                header.HashTableSize = hashBytes.Length;
                header.NameTableSize = nameBytes.Length;

                byte[] headerBytes = StructToBytes(header);
                PvfDecryptor.Decrypt("HeaD", headerBytes);
                if (_headerUsesGuard)
                    PvfDecryptor.DecryptGuard(headerBytes);

                using (var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
                {
                    outFs.Write(headerBytes, 0, 0x30);
                    outFs.Write(tableBytes, 0, tableBytes.Length);
                    outFs.Write(hashBytes, 0, hashBytes.Length);
                    outFs.Write(nameBytes, 0, nameBytes.Length);
                    outFs.Write(grpiBytes, 0, grpiBytes.Length);

                    using (var bodyIn = new FileStream(tempBodyPath, FileMode.Open, FileAccess.Read, FileShare.None, 256 * 1024))
                    {
                        byte[] copyBuf = new byte[256 * 1024];
                        int read;
                        while ((read = bodyIn.Read(copyBuf, 0, copyBuf.Length)) > 0)
                            outFs.Write(copyBuf, 0, read);
                    }
                }
            }
            finally
            {
                try { if (File.Exists(tempBodyPath)) File.Delete(tempBodyPath); } catch { }
            }
        }

        
        
        
        private byte[] RebuildChunkWithOverlay(int chunkIndex, byte[] originalChunk, PvfFileItem[] newItems)
        {
            
            var segments = new List<(int origOffset, int origSize, int fileIndex, byte[] newData)>();
            for (int i = 0; i < _files.Count; i++)
            {
                var item = _files[i].Entry;
                byte[] overlayData;
                bool hasOverlay = _overlay.TryGetValue(i, out overlayData);
                if (item.ChunkIndex != chunkIndex || (item.DataSize <= 0 && !hasOverlay)) continue;

                segments.Add((item.DataOffset, item.DataSize, i, hasOverlay ? overlayData : null));
            }
            segments.Sort((a, b) => a.origOffset.CompareTo(b.origOffset));

            var ms = new MemoryStream();
            int srcPos = 0;
            foreach (var seg in segments)
            {
                
                if (seg.origOffset > srcPos && originalChunk != null)
                    ms.Write(originalChunk, srcPos, seg.origOffset - srcPos);

                var item = newItems[seg.fileIndex];
                item.DataOffset = (int)ms.Position;

                if (seg.newData != null)
                {
                    ms.Write(seg.newData, 0, seg.newData.Length);
                    item.DataSize = seg.newData.Length;
                }
                else if (originalChunk != null && seg.origOffset >= 0 &&
                         seg.origOffset + seg.origSize <= originalChunk.Length)
                {
                    ms.Write(originalChunk, seg.origOffset, seg.origSize);
                }

                newItems[seg.fileIndex] = item;
                srcPos = seg.origOffset + seg.origSize;
            }

            
            if (originalChunk != null && srcPos < originalChunk.Length)
                ms.Write(originalChunk, srcPos, originalChunk.Length - srcPos);

            return ms.ToArray();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _strABuffer = null;
            _strWBuffer = null;
            _bodyBuffer = null;
            _rawTableBytes = null;
            _rawHashBytes = null;
            _rawNameBytes = null;
            _rawGrpiBytes = null;
            _strAOffsetCache = null;
            _strWOffsetCache = null;
            _files.Clear();
            _fileItems = null;
            _filesMaterialized = false;
            _compactPathIndex = false;
            _liteMapped = false;
            _liteFileCount = 0;
            _tableFileOffset = 0;
            _pathIndex.Clear();
            _groups.Clear();
            _overlay.Clear();
            _chunkCache.Clear();
            try { _mappedView?.Dispose(); } catch { /* ignore */ }
            try { _mappedFile?.Dispose(); } catch { /* ignore */ }
            _mappedView = null;
            _mappedFile = null;
        }

        /// <summary>
        /// Thread-safe LRU of decompressed chunk payloads bounded by total byte budget.
        /// </summary>
        private sealed class LruByteCache
        {
            private readonly object _gate = new object();
            private readonly long _budgetBytes;
            private readonly Dictionary<int, LinkedListNode<Entry>> _map = new Dictionary<int, LinkedListNode<Entry>>();
            private readonly LinkedList<Entry> _order = new LinkedList<Entry>();
            private long _size;

            public LruByteCache(long budgetBytes)
            {
                _budgetBytes = budgetBytes > 0 ? budgetBytes : DefaultChunkCacheBudgetBytes;
            }

            public byte[] GetOrAdd(int key, Func<int, byte[]> factory)
            {
                lock (_gate)
                {
                    if (_map.TryGetValue(key, out var existing))
                    {
                        _order.Remove(existing);
                        _order.AddFirst(existing);
                        return existing.Value.Data;
                    }
                }

                var created = factory(key);
                if (created == null)
                    return null;

                lock (_gate)
                {
                    if (_map.TryGetValue(key, out var raced))
                    {
                        _order.Remove(raced);
                        _order.AddFirst(raced);
                        return raced.Value.Data;
                    }

                    var entry = new Entry(key, created);
                    var node = _order.AddFirst(entry);
                    _map[key] = node;
                    _size += created.LongLength;
                    EvictIfNeeded();
                    return created;
                }
            }

            public void Clear()
            {
                lock (_gate)
                {
                    _map.Clear();
                    _order.Clear();
                    _size = 0;
                }
            }

            private void EvictIfNeeded()
            {
                while (_size > _budgetBytes && _order.Last != null)
                {
                    var last = _order.Last;
                    _order.RemoveLast();
                    _map.Remove(last.Value.Key);
                    _size -= last.Value.Data.LongLength;
                }
            }

            private readonly struct Entry
            {
                public Entry(int key, byte[] data)
                {
                    Key = key;
                    Data = data;
                }

                public int Key { get; }
                public byte[] Data { get; }
            }
        }
    }
}
