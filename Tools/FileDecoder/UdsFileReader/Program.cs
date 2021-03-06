﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UdsFileReader
{
    static class Program
    {
        static readonly Regex InvalidFileRegex = new Regex("^(R.|ReDir|TTDOP|MUX|TTText.*|Unit.*)$", RegexOptions.IgnoreCase);
        private static Dictionary<UInt32, UInt32> _unknownIdDict;

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length < 1)
            {
                Console.WriteLine("No input file specified");
                return 1;
            }

            string fileSpec = args[0];
            string dir = Path.GetDirectoryName(fileSpec);
            DirectoryInfo dirInfoParent = Directory.GetParent(dir);
            if (dirInfoParent == null)
            {
                Console.WriteLine("Invalid directory");
                return 1;
            }
            string rootDir = dirInfoParent.FullName;
            string searchPattern = Path.GetFileName(fileSpec);
            if (dir == null || searchPattern == null)
            {
                Console.WriteLine("Invalid file name");
                return 1;
            }

            try
            {
                UdsReader udsReader = new UdsReader();
                if (!udsReader.Init(rootDir))
                {
                    Console.WriteLine("Init failed");
                    return 1;
                }

                //Console.WriteLine(udsReader.TestFixedTypes());
                //return 0;
#if false
                UdsReader.FileNameResolver fileNameResolver = new UdsReader.FileNameResolver(udsReader, "EV_ECM20TDI01103L906018DQ", "003003", "03L906018DQ", "1K0907951");
                //UdsReader.FileNameResolver fileNameResolver = new UdsReader.FileNameResolver(udsReader, "EV_Kombi_UDS_VDD_RM09", "A04089", "0920881A", "1K0907951");
                List<string> fileList = fileNameResolver.GetFileList(dir);
                return 0;
#endif
#if false
                DataReader dataReader = new DataReader();
                DataReader.FileNameResolver fileNameResolver = new DataReader.FileNameResolver(dataReader, "03L906018DQ", 1);
                string fileName = fileNameResolver.GetFileName(Path.Combine(rootDir, DataReader.DataDir));
                if (!string.IsNullOrEmpty(fileName))
                {
                    List<DataReader.DataInfo> info = dataReader.ExtractDataType(fileName, DataReader.DataType.Settings);
                }
                return 0;
#endif
                _unknownIdDict = new Dictionary<UInt32, UInt32>();

                string[] files = Directory.GetFiles(dir, searchPattern, SearchOption.AllDirectories);
                foreach (string file in files)
                {
                    try
                    {
                        string fileExt = Path.GetExtension(file);
                        if (string.Compare(fileExt, UdsReader.FileExtension, StringComparison.OrdinalIgnoreCase) != 0)
                        {
                            continue;
                        }

                        string baseFile = Path.GetFileNameWithoutExtension(file);
                        if (baseFile == null || InvalidFileRegex.IsMatch(baseFile))
                        {
                            Console.WriteLine("Ignoring: {0}", file);
                            continue;
                        }
                        Console.WriteLine("Parsing: {0}", file);
                        string outFile = Path.ChangeExtension(file, ".txt");
                        if (outFile == null)
                        {
                            Console.WriteLine("*** Invalid output file");
                        }
                        else
                        {
                            using (StreamWriter outputStream = new StreamWriter(outFile, false, new UTF8Encoding(true)))
                            {
                                if (!ParseFile(udsReader, file, outputStream))
                                {
                                    Console.WriteLine("*** Parsing failed: {0}", file);
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("*** Exception {0}", e.Message);
                    }
                }

                if (_unknownIdDict.Count > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    Console.WriteLine();
                    Console.WriteLine("Unknown IDs:");
                    foreach (UInt32 key in _unknownIdDict.Keys.OrderBy(x => x))
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append(", ");
                        }
                        sb.Append($"{key}({_unknownIdDict[key]})");
                    }
                    sb.Insert(0, "Index: ");
                    Console.WriteLine(sb.ToString());
                    Console.WriteLine();

                    sb.Clear();
                    foreach (UInt32 key in _unknownIdDict.Keys.OrderByDescending(x => _unknownIdDict[x]))
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append(", ");
                        }
                        sb.Append($"{key}({_unknownIdDict[key]})");
                    }
                    sb.Insert(0, "Value: ");
                    Console.WriteLine(sb.ToString());
                }
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return 1;
            }
        }

        static bool ParseFile(UdsReader udsReader, string fileName, StreamWriter outStream)
        {
            try
            {
                List<string> includeFiles = udsReader.GetFileList(fileName);
                if (includeFiles == null)
                {
                    outStream.WriteLine("Get file list failed");
                    return false;
                }

                outStream.WriteLine("Includes:");
                foreach (string includeFile in includeFiles)
                {
                    outStream.WriteLine(includeFile);
                }

                foreach (UdsReader.SegmentType segmentType in Enum.GetValues(typeof(UdsReader.SegmentType)))
                {
                    List<UdsReader.ParseInfoBase> resultList = udsReader.ExtractFileSegment(includeFiles, segmentType);
                    if (resultList == null)
                    {
                        outStream.WriteLine("Parsing failed");
                        return false;
                    }

                    outStream.WriteLine();
                    outStream.WriteLine("Segment Type: {0}", segmentType.ToString());
                    outStream.WriteLine("-----------------------------------");
                    foreach (UdsReader.ParseInfoBase parseInfo in resultList)
                    {
                        outStream.WriteLine("");

                        StringBuilder sb = new StringBuilder();
                        foreach (string entry in parseInfo.LineArray)
                        {
                            if (sb.Length > 0)
                            {
                                sb.Append("; ");
                            }
                            sb.Append("\"");
                            sb.Append(entry);
                            sb.Append("\"");
                        }

                        sb.Insert(0, "Raw: ");
                        outStream.WriteLine(sb.ToString());

                        if (parseInfo is UdsReader.ParseInfoMwb parseInfoMwb)
                        {
                            sb.Clear();
                            foreach (string entry in parseInfoMwb.NameArray)
                            {
                                if (sb.Length > 0)
                                {
                                    sb.Append("; ");
                                }
                                sb.Append("\"");
                                sb.Append(entry);
                                sb.Append("\"");
                            }
                            sb.Insert(0, "Name: ");
                            outStream.WriteLine(sb.ToString());
                            outStream.WriteLine(string.Format(CultureInfo.InvariantCulture, "Service ID: {0:X04}", parseInfoMwb.ServiceId));

                            if (!PrintDataTypeEntry(outStream, parseInfoMwb.DataTypeEntry))
                            {
                                return false;
                            }

                            outStream.WriteLine(TestDataType(fileName, parseInfoMwb));
                        }

                        if (parseInfo is UdsReader.ParseInfoDtc parseInfoDtc)
                        {
                            sb.Clear();
                            outStream.WriteLine(string.Format(CultureInfo.InvariantCulture, "Error Code: {0} (0x{0:X06}), {1}", parseInfoDtc.ErrorCode, parseInfoDtc.PcodeText));
                            outStream.WriteLine(string.Format(CultureInfo.InvariantCulture, "Error Text: {0}", parseInfoDtc.ErrorText));
                            if (parseInfoDtc.DetailCode.HasValue && parseInfoDtc.DetailCode.Value > 0)
                            {
                                outStream.WriteLine(string.Format(CultureInfo.InvariantCulture, "Detail Code: {0:X02}", parseInfoDtc.DetailCode));
                            }
                            if (!string.IsNullOrEmpty(parseInfoDtc.ErrorDetail))
                            {
                                outStream.WriteLine(string.Format(CultureInfo.InvariantCulture, "Error Detail: {0}", parseInfoDtc.ErrorDetail));
                            }
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    outStream.WriteLine("Exception: {0}", ex.Message);
                }
                catch (Exception)
                {
                    // ignored
                }
                return false;
            }
        }

        static string TestDataType(string fileName, UdsReader.ParseInfoMwb parseInfoMwb)
        {
            UdsReader.DataTypeEntry dataTypeEntry = parseInfoMwb.DataTypeEntry;

            StringBuilder sb = new StringBuilder();

            sb.Append("Test: ");
            byte[] testData = null;
            if (Path.GetFileNameWithoutExtension(fileName) == "EV_ECM20TDI01103L906018DQ")
            {
                switch (parseInfoMwb.ServiceId)
                {
                    case 0xF40C:    // Motordrehzahl
                        testData = new byte[] { 0x0F, 0xA0 };
                        break;

                    case 0x11F1:    // Getriebeeingangsdrehzahl
                        testData = new byte[] { 0x07, 0xD0 };
                        break;

                    case 0x0100:    // Status des Stellgliedtests
                        testData = new byte[] { 0x80 };
                        break;

                    case 0xF40D:    // Fahrzeuggeschwindigkeit
                        testData = new byte[] { 0x64 };
                        break;

                    case 0x2001:    // Status der Kraftstofferstbefüllung
                        testData = new byte[] { 0xC0 };
                        break;

                    case 0xF41F:    // Zeit seit Motorstart
                        testData = new byte[] { 0x27, 0x10 };
                        break;

                    case 0x100D:    // eingelegter Gang
                        testData = new byte[] { 0x02 };
                        break;

                    case 0x02A7:    // SSEUI
                       testData = new byte[] { 0xE8, 0x03, 0xE9, 0x03, 0xEA, 0x03, 0xEB, 0x03, 0xEC, 0x03, 0xED, 0x03, 0xEE, 0x03, 0xEF, 0x03, 0xF0, 0x03, 0xF1, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
                       break;
                }
            }
            else if (Path.GetFileNameWithoutExtension(fileName) == "EV_Kombi_UDS_VDD_RM09_A04_VW32")
            {
                switch (parseInfoMwb.ServiceId)
                {
                    case 0xF442:    // Spannung Klemme 30
                        testData = new byte[] { 0x8F };
                        break;

                    case 0x2203:    // Wegstrecke
                        testData = new byte[] { 0x03, 0xE8 };
                        //testData = new byte[] { 0xFF, 0xFF };
                        break;
                }
            }

            if (testData != null)
            {
                sb.Append(" \"");
                sb.Append(dataTypeEntry.ToString(testData));
                sb.Append("\"");
            }
            else
            {
                sb.Append(" \"");
                sb.Append(dataTypeEntry.ToString(new byte[] { 0x10 }));
                sb.Append("\"");

                sb.Append(" \"");
                sb.Append(dataTypeEntry.ToString(new byte[] { 0x10, 0x20 }));
                sb.Append("\"");

                sb.Append(" \"");
                sb.Append(dataTypeEntry.ToString(new byte[] { 0xFF, 0x10 }));
                sb.Append("\"");

                sb.Append(" \"");
                sb.Append(dataTypeEntry.ToString(new byte[] { 0xFF, 0x10, 0x20 }));
                sb.Append("\"");

                sb.Append(" \"");
                sb.Append(dataTypeEntry.ToString(new byte[] { 0xFF, 0xAB, 0xCD }));
                sb.Append("\"");

                sb.Append(" \"");
                sb.Append(dataTypeEntry.ToString(new byte[] { 0xFF, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xCD, 0xEF, 0x01, 0x23, 0x45, 0x67, 0x89 }));
                sb.Append("\"");
            }

            return sb.ToString();
        }

        static bool PrintDataTypeEntry(StreamWriter outStream, UdsReader.DataTypeEntry dataTypeEntry)
        {
            StringBuilder sb = new StringBuilder();
            if (dataTypeEntry.NameDetailArray != null)
            {
                sb.Clear();
                foreach (string entry in dataTypeEntry.NameDetailArray)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append("; ");
                    }
                    sb.Append("\"");
                    sb.Append(entry);
                    sb.Append("\"");
                }

                sb.Insert(0, "Name Detail: ");
                outStream.WriteLine(sb.ToString());
            }

            sb.Clear();
            sb.Append(string.Format(CultureInfo.InvariantCulture, "Data type: {0}", UdsReader.DataTypeEntry.DataTypeIdToString(dataTypeEntry.DataTypeId)));

            if (dataTypeEntry.FixedEncodingId != null)
            {
                sb.Append($" (Fixed {dataTypeEntry.FixedEncodingId}");
                if (dataTypeEntry.FixedEncoding == null)
                {
                    sb.Append(" ,Unknown ID");
                    if (_unknownIdDict.TryGetValue(dataTypeEntry.FixedEncodingId.Value, out UInt32 oldValue))
                    {
                        _unknownIdDict[dataTypeEntry.FixedEncodingId.Value] = oldValue + 1;
                    }
                    else
                    {
                        _unknownIdDict[dataTypeEntry.FixedEncodingId.Value] = 1;
                    }
                }
                else
                {
                    if (dataTypeEntry.FixedEncoding.ConvertFunc != null)
                    {
                        sb.Append(" ,Function");
                    }
                }
                sb.Append(")");
            }

            if (dataTypeEntry.NumberOfDigits.HasValue)
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture, "; Digits: {0}", dataTypeEntry.NumberOfDigits.Value));
            }

            if (dataTypeEntry.ScaleOffset.HasValue)
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture, "; Offset: {0}", dataTypeEntry.ScaleOffset.Value));
            }

            if (dataTypeEntry.ScaleMult.HasValue)
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture, "; Mult: {0}", dataTypeEntry.ScaleMult.Value));
            }

            if (dataTypeEntry.ScaleDiv.HasValue)
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture, "; Div: {0}", dataTypeEntry.ScaleDiv.Value));
            }

            if (dataTypeEntry.UnitText != null)
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture, "; Unit: \"{0}\"", dataTypeEntry.UnitText));
            }

            if (dataTypeEntry.ByteOffset.HasValue)
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture, "; Byte: {0}", dataTypeEntry.ByteOffset.Value));
            }

            if (dataTypeEntry.BitOffset.HasValue)
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture, "; Bit: {0}", dataTypeEntry.BitOffset.Value));
            }

            if (dataTypeEntry.BitLength.HasValue)
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture, "; Len: {0}", dataTypeEntry.BitLength.Value));
            }

            outStream.WriteLine(sb.ToString());

            if (dataTypeEntry.NameValueList != null)
            {
                foreach (UdsReader.ValueName valueName in dataTypeEntry.NameValueList)
                {
                    sb.Clear();

                    foreach (string entry in valueName.LineArray)
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append("; ");
                        }
                        sb.Append("\"");
                        sb.Append(entry);
                        sb.Append("\"");
                    }

                    if (valueName.NameArray != null)
                    {
                        sb.Append(": ");
                        foreach (string nameEntry in valueName.NameArray)
                        {
                            sb.Append("\"");
                            sb.Append(nameEntry);
                            sb.Append("\" ");
                        }
                    }

                    sb.Insert(0, "Value Name: ");
                    outStream.WriteLine(sb.ToString());
                }
            }

            if (dataTypeEntry.MuxEntryList != null)
            {
                foreach (UdsReader.MuxEntry muxEntry in dataTypeEntry.MuxEntryList)
                {
                    sb.Clear();

                    foreach (string entry in muxEntry.LineArray)
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append("; ");
                        }
                        sb.Append("\"");
                        sb.Append(entry);
                        sb.Append("\"");
                    }
                    sb.Insert(0, "Mux: ");
                    outStream.WriteLine(sb.ToString());

                    sb.Clear();
                    if (muxEntry.Default)
                    {
                        sb.Append(string.Format(CultureInfo.InvariantCulture, "Default"));
                    }

                    if (muxEntry.MinValue != null)
                    {
                        sb.Append(string.Format(CultureInfo.InvariantCulture, "Min: {0}", muxEntry.MinValue));
                    }

                    if (muxEntry.MaxValue != null)
                    {
                        sb.Append(string.Format(CultureInfo.InvariantCulture, " Max: {0}", muxEntry.MaxValue));
                    }

                    if (sb.Length > 0)
                    {
                        outStream.WriteLine(sb.ToString());
                    }

                    if (muxEntry.DataTypeEntry != null)
                    {
                        if (!PrintDataTypeEntry(outStream, muxEntry.DataTypeEntry))
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }
    }
}
