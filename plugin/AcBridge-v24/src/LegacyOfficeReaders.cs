using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Civil3DMcpPlugin;

/// <summary>
/// Zero-dependency readers for legacy Office binary formats:
///   - Word 97-2003 (.doc)   -> WordDocument stream + table stream (CLX / piece table)
///   - Excel 97-2003 (.xls)  -> Workbook/Book stream (BIFF8 records, first worksheet)
/// Only BCL (System.*) types are used; no NuGet, no AutoCAD types, no Office COM.
/// Failures throw plain Exception / InvalidDataException.
/// </summary>
internal static class LegacyOfficeReaders
{
    // Output marker appended when the worksheet is truncated by maxRows. ASCII-escaped source.
    private const string TruncationMarker = "...\uff08\u5df2\u622a\u65ad\uff09";

    // ---------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------

    /// <summary>Extract the plain text of a Word 97-2003 (.doc) file.</summary>
    public static string ReadDocText(byte[] file)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        if (file.Length == 0) throw new InvalidDataException("Empty file.");

        var cfb = new CfbDocument(file);
        byte[] wordDoc = cfb.GetStreamRequired("WordDocument");
        if (wordDoc.Length < 0x40) throw new InvalidDataException("WordDocument stream is too small.");

        ushort wIdent = BitConverter.ToUInt16(wordDoc, 0);
        if (wIdent != 0xA5EC) throw new InvalidDataException("Not a Word binary document (bad wIdent).");

        ushort nFib = BitConverter.ToUInt16(wordDoc, 2);
        if (nFib < 0x00C1) throw new InvalidDataException("Unsupported Word version (nFib=0x" + nFib.ToString("X4") + "); Word 97 or newer required.");

        ushort fibFlags = BitConverter.ToUInt16(wordDoc, 10);
        if ((fibFlags & 0x0100) != 0) throw new InvalidDataException("Encrypted .doc files are not supported.");

        bool fWhichTblStm = (fibFlags & 0x0200) != 0;
        string tableName = fWhichTblStm ? "1Table" : "0Table";
        byte[]? table = cfb.GetStream(tableName);
        if (table == null)
        {
            table = cfb.GetStream(fWhichTblStm ? "0Table" : "1Table");
            if (table == null) throw new InvalidDataException("Table stream ('" + tableName + "') not found.");
        }

        // Locate FibRgFcLcb97: FibBase(32) + [csw(2)+FibRgW97] + [cslw(2)+FibRgLw97] + cbRgFcLcb(2) + pairs(8 each).
        int csw = BitConverter.ToUInt16(wordDoc, 0x20);
        int off = 0x22 + csw * 2;
        if (off + 2 > wordDoc.Length) throw new InvalidDataException("Malformed FIB (cslw).");
        int cslw = BitConverter.ToUInt16(wordDoc, off);
        off += 2 + cslw * 4;
        off += 2; // skip cbRgFcLcb
        int fcClxOff = off + 66 * 4; // fcClx is the 67th (fc,lcb) pair in FibRgFcLcb97
        if (fcClxOff + 8 > wordDoc.Length) throw new InvalidDataException("Malformed FIB (fcClx).");
        int fcClx = BitConverter.ToInt32(wordDoc, fcClxOff);
        int lcbClx = BitConverter.ToInt32(wordDoc, fcClxOff + 4);
        if (fcClx <= 0 || lcbClx <= 0 || fcClx + lcbClx > table.Length)
            throw new InvalidDataException("Invalid CLX location in table stream.");

        int ccpText = 0;
        if (0x4C + 4 <= wordDoc.Length) ccpText = BitConverter.ToInt32(wordDoc, 0x4C);
        if (ccpText < 0) ccpText = 0;

        List<TextPiece> pieces = ParseClx(table, fcClx, lcbClx);
        if (pieces.Count == 0) throw new InvalidDataException("No text pieces found in CLX.");

        var sb = new StringBuilder(ccpText > 0 && ccpText < 4_000_000 ? ccpText : 4096);
        int produced = 0;
        int fieldDepth = 0;
        bool inFieldCode = false;

        foreach (TextPiece pc in pieces)
        {
            int len = pc.CpEnd - pc.CpStart;
            if (len <= 0) continue;
            int baseFc = pc.Fc;
            bool compressed = pc.Compressed;

            for (int i = 0; i < len; i++)
            {
                if (ccpText > 0 && produced >= ccpText) break;
                produced++;

                char ch;
                if (compressed)
                {
                    int p = baseFc + i;
                    ch = (p >= 0 && p < wordDoc.Length) ? (char)wordDoc[p] : '\uFFFD';
                }
                else
                {
                    int p = baseFc + i * 2;
                    ch = (p >= 0 && p + 1 < wordDoc.Length)
                        ? (char)(wordDoc[p] | (wordDoc[p + 1] << 8))
                        : '\uFFFD';
                }

                // Field markers: 0x13 begin, 0x14 separator, 0x15 end.
                if (ch == '\u0013') { fieldDepth++; if (fieldDepth == 1) inFieldCode = true; continue; }
                if (ch == '\u0014') { if (fieldDepth > 0) inFieldCode = false; continue; }
                if (ch == '\u0015') { if (fieldDepth > 0) fieldDepth--; if (fieldDepth == 0) inFieldCode = false; continue; }
                if (inFieldCode) continue;

                switch (ch)
                {
                    case '\r':      // paragraph end
                    case '\u000B':  // manual line break
                    case '\u000C':  // page/section break
                        sb.Append('\n');
                        break;
                    case '\u0007':  // cell / row mark
                    case '\t':
                        sb.Append('\t');
                        break;
                    case '\u00A0':  // non-breaking space
                        sb.Append(' ');
                        break;
                    case '\uFFFC':  // inline object placeholder
                        break;
                    default:
                        if (ch < 0x20) { /* drop remaining control chars */ }
                        else sb.Append(ch);
                        break;
                }
            }

            if (ccpText > 0 && produced >= ccpText) break;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Extract the text of the first worksheet of an Excel 97-2003 (.xls) file.
    /// Cells within a row are separated by TAB, rows by LF. When maxRows &gt; 0 the
    /// output is limited to that many rows and a truncation marker line is appended;
    /// maxRows &lt;= 0 means "all rows".
    /// </summary>
    public static string ReadXlsText(byte[] file, int maxRows)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        if (file.Length == 0) throw new InvalidDataException("Empty file.");

        var cfb = new CfbDocument(file);
        byte[]? wb = cfb.GetStream("Workbook");
        if (wb == null) wb = cfb.GetStream("Book");
        if (wb == null) throw new InvalidDataException("Workbook/Book stream not found.");
        if (wb.Length < 8) throw new InvalidDataException("Workbook stream is too small.");

        return XlsWorkbook.ReadFirstSheet(wb, maxRows);
    }

    // ---------------------------------------------------------------------
    // .doc CLX / piece table
    // ---------------------------------------------------------------------

    private readonly struct TextPiece
    {
        public readonly int CpStart;
        public readonly int CpEnd;
        public readonly int Fc;
        public readonly bool Compressed;

        public TextPiece(int cpStart, int cpEnd, int fc, bool compressed)
        {
            CpStart = cpStart;
            CpEnd = cpEnd;
            Fc = fc;
            Compressed = compressed;
        }
    }

    private static List<TextPiece> ParseClx(byte[] table, int fcClx, int lcbClx)
    {
        var result = new List<TextPiece>();
        int end = fcClx + lcbClx;
        int p = fcClx;

        while (p < end)
        {
            byte t = table[p];
            if (t == 0x01)
            {
                // clxtGrpprl: 2-byte size + grpprl
                if (p + 3 > end) break;
                int cb = BitConverter.ToUInt16(table, p + 1);
                p += 3 + cb;
            }
            else if (t == 0x02)
            {
                // clxtPlcfpcd: 4-byte size + PlcPcd
                if (p + 5 > end) break;
                int lcb = BitConverter.ToInt32(table, p + 1);
                p += 5;
                if (lcb < 4 || p + lcb > table.Length) throw new InvalidDataException("PlcPcd out of range.");
                int n = (lcb - 4) / 12;
                if (n > 0)
                {
                    int baseOff = p;
                    for (int i = 0; i < n; i++)
                    {
                        int cpStart = BitConverter.ToInt32(table, baseOff + i * 4);
                        int cpEnd = BitConverter.ToInt32(table, baseOff + (i + 1) * 4);
                        int pcdOff = baseOff + (n + 1) * 4 + i * 8;
                        int fcRaw = BitConverter.ToInt32(table, pcdOff + 2);
                        int fc = fcRaw & 0x3FFFFFFF;
                        bool compressed = (fcRaw & 0x40000000) != 0;
                        if (compressed) fc /= 2;
                        result.Add(new TextPiece(cpStart, cpEnd, fc, compressed));
                    }
                }
                p += lcb;
            }
            else
            {
                break;
            }
        }

        return result;
    }

    // ---------------------------------------------------------------------
    // .xls BIFF8
    // ---------------------------------------------------------------------

    private static class XlsWorkbook
    {
        private const int Bof = 0x0809;
        private const int Eof = 0x000A;
        private const int BoundSheet = 0x0085;
        private const int Continue = 0x003C;
        private const int Sst = 0x00FC;
        private const int LabelSst = 0x00FD;
        private const int Label = 0x0204;
        private const int Number = 0x0203;
        private const int Rk = 0x027E;
        private const int MulRk = 0x00BD;
        private const int Formula = 0x0006;
        private const int StringRec = 0x0207;

        public static string ReadFirstSheet(byte[] wb, int maxRows)
        {
            bool biff5 = false;
            if (BitConverter.ToUInt16(wb, 0) == Bof)
            {
                if (wb.Length >= 8) biff5 = BitConverter.ToUInt16(wb, 4) < 0x0600;
            }

            var sheets = new List<SheetRef>();
            List<byte[]>? sstSegments = null;

            int pos = 0;
            while (pos + 4 <= wb.Length)
            {
                int id = BitConverter.ToUInt16(wb, pos);
                int len = BitConverter.ToUInt16(wb, pos + 2);
                int dataOff = pos + 4;
                if (dataOff + len > wb.Length) break;

                if (id == BoundSheet && len >= 8)
                {
                    int lbPly = BitConverter.ToInt32(wb, dataOff);
                    byte cch = wb[dataOff + 6];
                    byte grbit = wb[dataOff + 7];
                    int nameOff = dataOff + 8;
                    string nm;
                    if ((grbit & 0x01) != 0)
                    {
                        int n = Math.Min(cch, Math.Max(0, (len - 8) / 2));
                        nm = Encoding.Unicode.GetString(wb, nameOff, n * 2);
                    }
                    else
                    {
                        int n = Math.Min(cch, Math.Max(0, len - 8));
                        nm = Encoding.Latin1.GetString(wb, nameOff, n);
                    }
                    sheets.Add(new SheetRef(lbPly, nm));
                }
                else if (id == Sst)
                {
                    sstSegments = new List<byte[]> { Slice(wb, dataOff, len) };
                    int q = dataOff + len;
                    while (q + 4 <= wb.Length && BitConverter.ToUInt16(wb, q) == Continue)
                    {
                        int clen = BitConverter.ToUInt16(wb, q + 2);
                        sstSegments.Add(Slice(wb, q + 4, clen));
                        q += 4 + clen;
                    }
                }

                pos = dataOff + len;
            }

            List<string> sst = (sstSegments == null || biff5) ? new List<string>() : ParseSst(sstSegments);
            if (sheets.Count == 0) throw new InvalidDataException("No worksheet (BOUNDSHEET) found in workbook.");

            var rows = new SortedDictionary<int, SortedDictionary<int, string>>();
            ParseSheet(wb, sheets[0].Position, sst, biff5, rows);
            return FormatRows(rows, maxRows);
        }

        private readonly struct SheetRef
        {
            public readonly int Position;
            public readonly string Name;
            public SheetRef(int position, string name) { Position = position; Name = name; }
        }

        private static List<string> ParseSst(List<byte[]> segments)
        {
            var reader = new SstReader(segments);
            int total = reader.ReadInt32();
            int unique = reader.ReadInt32();
            if (unique < 0 || unique > 20_000_000) throw new InvalidDataException("Invalid SST unique count (" + unique + ").");
            _ = total;
            var list = new List<string>(unique);
            for (int i = 0; i < unique; i++) list.Add(reader.ReadString());
            return list;
        }

        private static void ParseSheet(byte[] wb, int start, List<string> sst, bool biff5, SortedDictionary<int, SortedDictionary<int, string>> rows)
        {
            int pos = start;
            int pendingRow = -1;
            int pendingCol = -1;
            bool pendingFormulaString = false;

            while (pos + 4 <= wb.Length)
            {
                int id = BitConverter.ToUInt16(wb, pos);
                int len = BitConverter.ToUInt16(wb, pos + 2);
                int d = pos + 4;
                if (d + len > wb.Length) break;
                if (id == Eof) break;

                switch (id)
                {
                    case LabelSst:
                        if (len >= 10)
                        {
                            int r = BitConverter.ToUInt16(wb, d);
                            int c = BitConverter.ToUInt16(wb, d + 2);
                            int isst = BitConverter.ToInt32(wb, d + 6);
                            SetCell(rows, r, c, (isst >= 0 && isst < sst.Count) ? sst[isst] : string.Empty);
                        }
                        break;

                    case Label:
                        if (len >= 8)
                        {
                            int r = BitConverter.ToUInt16(wb, d);
                            int c = BitConverter.ToUInt16(wb, d + 2);
                            string s = biff5 ? ReadBiff5String(wb, d + 6, len - 6) : ReadXlUnicodeString(wb, d + 6, len - 6);
                            SetCell(rows, r, c, s);
                        }
                        break;

                    case Rk:
                        if (len >= 10)
                        {
                            int r = BitConverter.ToUInt16(wb, d);
                            int c = BitConverter.ToUInt16(wb, d + 2);
                            SetCell(rows, r, c, NumberToString(DecodeRk(BitConverter.ToInt32(wb, d + 6))));
                        }
                        break;

                    case MulRk:
                        if (len >= 6)
                        {
                            int r = BitConverter.ToUInt16(wb, d);
                            int cFirst = BitConverter.ToUInt16(wb, d + 2);
                            int count = (len - 6) / 6;
                            for (int i = 0; i < count; i++)
                            {
                                int rkOff = d + 4 + i * 6 + 2;
                                if (rkOff + 4 > d + len) break;
                                int c = cFirst + i;
                                SetCell(rows, r, c, NumberToString(DecodeRk(BitConverter.ToInt32(wb, rkOff))));
                            }
                        }
                        break;

                    case Number:
                        if (len >= 14)
                        {
                            int r = BitConverter.ToUInt16(wb, d);
                            int c = BitConverter.ToUInt16(wb, d + 2);
                            SetCell(rows, r, c, NumberToString(BitConverter.ToDouble(wb, d + 6)));
                        }
                        break;

                    case Formula:
                        if (len >= 14)
                        {
                            int r = BitConverter.ToUInt16(wb, d);
                            int c = BitConverter.ToUInt16(wb, d + 2);
                            pendingRow = r;
                            pendingCol = c;
                            pendingFormulaString = true;
                            SetCell(rows, r, c, NumberToString(BitConverter.ToDouble(wb, d + 6)));
                        }
                        break;

                    case StringRec:
                        if (pendingFormulaString && pendingRow >= 0)
                        {
                            string s = biff5 ? ReadBiff5String(wb, d, len) : ReadXlUnicodeString(wb, d, len);
                            SetCell(rows, pendingRow, pendingCol, s);
                            pendingFormulaString = false;
                        }
                        break;

                    default:
                        // A string result of a formula is immediately followed by a STRING record;
                        // any other intervening record cancels the pending state.
                        if (id != Continue) pendingFormulaString = false;
                        break;
                }

                pos = d + len;
            }
        }

        // BIFF8 XLUnicodeString: 2-byte char count, 1-byte grbit (bit0 = 16-bit chars).
        private static string ReadXlUnicodeString(byte[] wb, int off, int maxLen)
        {
            if (maxLen < 3) return string.Empty;
            int cch = BitConverter.ToUInt16(wb, off);
            byte grbit = wb[off + 2];
            int p = off + 3;
            if ((grbit & 0x01) != 0)
            {
                int n = Math.Min(cch, (maxLen - 3) / 2);
                if (n <= 0) return string.Empty;
                return Encoding.Unicode.GetString(wb, p, n * 2);
            }
            else
            {
                int n = Math.Min(cch, maxLen - 3);
                if (n <= 0) return string.Empty;
                return Encoding.Latin1.GetString(wb, p, n);
            }
        }

        // BIFF5 byte string: 1-byte char count, Latin-1 chars.
        private static string ReadBiff5String(byte[] wb, int off, int maxLen)
        {
            if (maxLen < 1) return string.Empty;
            int cch = wb[off];
            int n = Math.Min(cch, maxLen - 1);
            if (n <= 0) return string.Empty;
            return Encoding.Latin1.GetString(wb, off + 1, n);
        }

        private static void SetCell(SortedDictionary<int, SortedDictionary<int, string>> rows, int row, int col, string value)
        {
            if (row < 0 || col < 0) return;
            if (!rows.TryGetValue(row, out SortedDictionary<int, string>? cols))
            {
                cols = new SortedDictionary<int, string>();
                rows[row] = cols;
            }
            cols[col] = value;
        }

        private static string FormatRows(SortedDictionary<int, SortedDictionary<int, string>> rows, int maxRows)
        {
            var lines = new List<string>();
            bool truncated = false;
            int count = 0;

            foreach (KeyValuePair<int, SortedDictionary<int, string>> kv in rows)
            {
                if (maxRows > 0 && count >= maxRows) { truncated = true; break; }
                var sb = new StringBuilder();
                bool first = true;
                foreach (KeyValuePair<int, string> cell in kv.Value)
                {
                    if (!first) sb.Append('\t');
                    sb.Append(cell.Value);
                    first = false;
                }
                lines.Add(sb.ToString());
                count++;
            }

            if (truncated) lines.Add(TruncationMarker);
            return string.Join("\n", lines);
        }

        private static double DecodeRk(int rk)
        {
            bool x100 = (rk & 0x01) != 0;
            bool isInt = (rk & 0x02) != 0;
            double val;
            if (isInt)
            {
                val = rk >> 2; // arithmetic shift -> signed 30-bit integer
            }
            else
            {
                long bits = ((long)(rk & unchecked((int)0xFFFFFFFC))) << 32;
                val = BitConverter.Int64BitsToDouble(bits);
            }
            if (x100) val /= 100.0;
            return val;
        }

        private static string NumberToString(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return string.Empty;
            if (v == Math.Floor(v) && Math.Abs(v) < 1e15)
                return ((long)v).ToString(CultureInfo.InvariantCulture);
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        // Streaming reader over SST + CONTINUE segments. The tricky part: when a string's
        // character data continues into a CONTINUE record, that record starts with a
        // 1-byte grbit (fHighByte) that must be consumed before the remaining characters.
        private sealed class SstReader
        {
            private readonly List<byte[]> _segments;
            private int _seg;
            private int _pos;

            public SstReader(List<byte[]> segments)
            {
                _segments = segments;
                _seg = 0;
                _pos = 0;
            }

            private bool Ensure()
            {
                while (_pos >= _segments[_seg].Length)
                {
                    if (_seg + 1 >= _segments.Count) return false;
                    _seg++;
                    _pos = 0;
                }
                return true;
            }

            private byte ReadRawByte()
            {
                if (!Ensure()) throw new InvalidDataException("SST record truncated.");
                return _segments[_seg][_pos++];
            }

            public ushort ReadUInt16() { int lo = ReadRawByte(); int hi = ReadRawByte(); return (ushort)(lo | (hi << 8)); }

            public int ReadInt32()
            {
                int b0 = ReadRawByte();
                int b1 = ReadRawByte();
                int b2 = ReadRawByte();
                int b3 = ReadRawByte();
                return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
            }

            private void Skip(int count)
            {
                for (int i = 0; i < count; i++) ReadRawByte();
            }

            public string ReadString()
            {
                int cch = ReadUInt16();
                byte grbit = ReadRawByte();
                bool fHigh = (grbit & 0x01) != 0;
                bool fExt = (grbit & 0x04) != 0;
                bool fRich = (grbit & 0x08) != 0;
                int cRun = fRich ? ReadUInt16() : 0;
                int cbExt = fExt ? ReadInt32() : 0;

                var sb = new StringBuilder(cch);
                for (int i = 0; i < cch; i++)
                {
                    // Cross a record boundary: the CONTINUE record begins with a grbit byte.
                    if (_pos >= _segments[_seg].Length)
                    {
                        if (_seg + 1 >= _segments.Count) throw new InvalidDataException("SST string truncated.");
                        _seg++;
                        _pos = 0;
                        byte g = _segments[_seg][_pos++];
                        fHigh = (g & 0x01) != 0;
                    }

                    if (fHigh)
                    {
                        if (_pos + 2 > _segments[_seg].Length)
                        {
                            if (_seg + 1 >= _segments.Count) throw new InvalidDataException("SST UTF-16 char truncated.");
                            _seg++;
                            _pos = 0;
                            byte g = _segments[_seg][_pos++];
                            fHigh = (g & 0x01) != 0;
                        }
                        int lo = _segments[_seg][_pos++];
                        int hi = _segments[_seg][_pos++];
                        sb.Append((char)(lo | (hi << 8)));
                    }
                    else
                    {
                        sb.Append((char)_segments[_seg][_pos++]);
                    }
                }

                if (fRich) Skip(cRun * 4);
                if (fExt) Skip(cbExt);
                return sb.ToString();
            }
        }
    }

    // ---------------------------------------------------------------------
    // OLE2 / Compound File Binary reader
    // ---------------------------------------------------------------------

    private sealed class CfbDocument
    {
        private const uint FreeSect = 0xFFFFFFFF;
        private const uint EndOfChain = 0xFFFFFFFE;
        private const uint FatSect = 0xFFFFFFFD;

        private readonly byte[] _data;
        private readonly int _sectorSize;
        private readonly int _miniSectorSize;
        private readonly int _majorVersion;
        private readonly int _miniCutoff;
        private readonly uint[] _fat;
        private readonly uint[] _miniFat;
        private readonly byte[] _miniStream;
        private readonly List<CfbDirEntry> _entries = new List<CfbDirEntry>();

        public CfbDocument(byte[] data)
        {
            _data = data;

            if (data.Length < 512) throw new InvalidDataException("Not an OLE2 file (too small).");
            if (BitConverter.ToUInt32(data, 0) != 0xE011CFD0 || BitConverter.ToUInt32(data, 4) != 0xE11AB1A1)
                throw new InvalidDataException("Not an OLE2 compound document (bad signature).");

            int sectorShift = BitConverter.ToUInt16(data, 30);
            int miniSectorShift = BitConverter.ToUInt16(data, 32);
            if (sectorShift != 9 && sectorShift != 12)
                throw new InvalidDataException("Unsupported OLE2 sector size (shift=" + sectorShift + ").");
            if (miniSectorShift != 6)
                throw new InvalidDataException("Unsupported OLE2 mini sector size (shift=" + miniSectorShift + ").");

            _sectorSize = 1 << sectorShift;
            _miniSectorSize = 1 << miniSectorShift;
            _majorVersion = BitConverter.ToUInt16(data, 26);

            int firstDirSector = (int)BitConverter.ToUInt32(data, 48);
            _miniCutoff = (int)BitConverter.ToUInt32(data, 56);
            int firstMiniFatSector = (int)BitConverter.ToUInt32(data, 60);
            int numMiniFatSectors = (int)BitConverter.ToUInt32(data, 64);
            int firstDifatSector = (int)BitConverter.ToUInt32(data, 68);
            if (_miniCutoff <= 0) _miniCutoff = 4096;

            // Gather FAT sector numbers from the header DIFAT and any DIFAT sectors.
            var fatSectors = new List<int>();
            for (int i = 0; i < 109; i++)
            {
                uint v = BitConverter.ToUInt32(data, 76 + i * 4);
                if (v == FreeSect || v == EndOfChain) continue;
                fatSectors.Add((int)v);
            }

            int difat = firstDifatSector;
            int guard = 0;
            while (IsSector(difat) && guard++ < 4096)
            {
                int off = SectorOffset(difat);
                if (off + _sectorSize > data.Length) break;
                int n = _sectorSize / 4 - 1;
                for (int i = 0; i < n; i++)
                {
                    uint v = BitConverter.ToUInt32(data, off + i * 4);
                    if (v != FreeSect && v != EndOfChain) fatSectors.Add((int)v);
                }
                difat = (int)BitConverter.ToUInt32(data, off + n * 4);
            }

            _fat = new uint[fatSectors.Count * (_sectorSize / 4)];
            int fi = 0;
            foreach (int fs in fatSectors)
            {
                int off = SectorOffset(fs);
                if (off + _sectorSize > data.Length) throw new InvalidDataException("FAT sector out of range.");
                for (int i = 0; i < _sectorSize / 4; i++) _fat[fi++] = BitConverter.ToUInt32(data, off + i * 4);
            }

            if (numMiniFatSectors > 0 && IsSector(firstMiniFatSector))
            {
                byte[] mf = ReadFatChain(firstMiniFatSector);
                _miniFat = new uint[mf.Length / 4];
                for (int i = 0; i < _miniFat.Length; i++) _miniFat[i] = BitConverter.ToUInt32(mf, i * 4);
            }
            else
            {
                _miniFat = Array.Empty<uint>();
            }

            byte[] dir = ReadFatChain(firstDirSector);
            for (int off = 0; off + 128 <= dir.Length; off += 128)
            {
                CfbDirEntry? e = ParseDirEntry(dir, off);
                if (e != null) _entries.Add(e);
            }
            if (_entries.Count == 0) throw new InvalidDataException("OLE2 directory is empty.");

            CfbDirEntry root = _entries[0];
            if (root.Type == 5 && root.Size > 0 && IsSector((int)root.StartSector))
                _miniStream = ReadFatChain((int)root.StartSector);
            else
                _miniStream = Array.Empty<byte>();
        }

        private static bool IsSector(int s) => s >= 0 && (uint)s < 0xFFFFFFFCu;

        private int SectorOffset(int sector) => (sector + 1) * _sectorSize;

        private byte[] ReadFatChain(int startSector)
        {
            if (!IsSector(startSector)) return Array.Empty<byte>();
            var ms = new MemoryStream();
            int sector = startSector;
            int guard = 0;
            while (IsSector(sector))
            {
                int off = SectorOffset(sector);
                if (off + _sectorSize > _data.Length) break;
                ms.Write(_data, off, _sectorSize);
                if (sector >= _fat.Length) break;
                sector = (int)_fat[sector];
                if (guard++ > 10_000_000) break;
            }
            return ms.ToArray();
        }

        private static CfbDirEntry? ParseDirEntry(byte[] dir, int off)
        {
            byte type = dir[off + 66];
            if (type == 0) return null;

            int nameLenBytes = BitConverter.ToUInt16(dir, off + 64);
            int chars = nameLenBytes / 2;
            if (chars > 0) chars -= 1; // exclude trailing null
            if (chars < 0) chars = 0;
            if (chars > 31) chars = 31;
            string name = chars > 0 ? Encoding.Unicode.GetString(dir, off, chars * 2) : string.Empty;

            uint start = BitConverter.ToUInt32(dir, off + 116);
            ulong size = BitConverter.ToUInt64(dir, off + 120);
            if (type != 5 && size > 0x7FFFFFFF) size = 0x7FFFFFFF; // sanity guard
            return new CfbDirEntry(name, type, start, size);
        }

        public byte[]? GetStream(string name)
        {
            foreach (CfbDirEntry e in _entries)
            {
                if (e.Type == 2 && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                    return ReadEntry(e);
            }
            return null;
        }

        public byte[] GetStreamRequired(string name) =>
            GetStream(name) ?? throw new InvalidDataException("Stream not found: " + name);

        private byte[] ReadEntry(CfbDirEntry e)
        {
            int size = (int)e.Size;
            if (size <= 0) return Array.Empty<byte>();
            if (!IsSector((int)e.StartSector)) return Array.Empty<byte>();

            if (size < _miniCutoff && e.Type != 5)
            {
                // Mini stream: 64-byte sectors resolved through the MiniFAT.
                if (_miniFat.Length == 0) throw new InvalidDataException("Mini stream referenced but MiniFAT is empty.");
                var ms = new MemoryStream();
                int sec = (int)e.StartSector;
                int guard = 0;
                while (IsSector(sec))
                {
                    int off = sec * _miniSectorSize;
                    if (off + _miniSectorSize > _miniStream.Length) break;
                    ms.Write(_miniStream, off, _miniSectorSize);
                    if (sec >= _miniFat.Length) break;
                    sec = (int)_miniFat[sec];
                    if (guard++ > 10_000_000) break;
                }
                byte[] arr = ms.ToArray();
                if (arr.Length > size) Array.Resize(ref arr, size);
                return arr;
            }
            else
            {
                byte[] arr = ReadFatChain((int)e.StartSector);
                if (arr.Length > size) Array.Resize(ref arr, size);
                return arr;
            }
        }
    }

    private sealed class CfbDirEntry
    {
        public readonly string Name;
        public readonly byte Type;
        public readonly uint StartSector;
        public readonly ulong Size;

        public CfbDirEntry(string name, byte type, uint startSector, ulong size)
        {
            Name = name;
            Type = type;
            StartSector = startSector;
            Size = size;
        }
    }

    private static byte[] Slice(byte[] src, int off, int len)
    {
        if (off < 0) off = 0;
        if (off > src.Length) return Array.Empty<byte>();
        if (off + len > src.Length) len = src.Length - off;
        if (len <= 0) return Array.Empty<byte>();
        var b = new byte[len];
        Buffer.BlockCopy(src, off, b, 0, len);
        return b;
    }
}
