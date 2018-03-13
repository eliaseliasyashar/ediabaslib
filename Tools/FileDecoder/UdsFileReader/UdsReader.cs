﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace UdsFileReader
{
    public class UdsReader
    {
        private static readonly Encoding Encoding = Encoding.GetEncoding(1252);
        public const string FileExtension = ".rodtxt";

        public enum SegmentType
        {
            Adp,
            Dtc,
            Ffmux,
            Ges,
            Mwb,
            Sot,
            Xpl,
        }

        public enum DataType
        {
            Float = 0,
            Integer = 2,
            ValueName = 3,
            ExtTable = 4,
            Binary = 5,
            HexBytes = 7,
            String = 8,
        }

        public const int DataTypeMaskSwapped = 0x40;
        public const int DataTypeMaskUnit = 0x80;
        public const int DataTypeMaskEnum = 0x3F;

        public class ValueName
        {
            public ValueName(UdsReader udsReader, string[] lineArray)
            {
                LineArray = lineArray;

                if (lineArray.Length >= 5)
                {
                    object valueObjMin = new Int32Converter().ConvertFromInvariantString(lineArray[1]);
                    if (valueObjMin != null)
                    {
                        MinValue = (Int32)valueObjMin;
                    }

                    object valueObjMax = new Int32Converter().ConvertFromInvariantString(lineArray[2]);
                    if (valueObjMax != null)
                    {
                        MaxValue = (Int32)valueObjMax;
                    }

                    if (UInt32.TryParse(lineArray[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 valueNameKey))
                    {
                        if (udsReader._textMap.TryGetValue(valueNameKey, out string[] nameValueArray))
                        {
                            NameArray = nameValueArray;
                        }
                    }
                }
            }

            public string[] LineArray { get; }
            public string[] NameArray { get; }
            public Int32? MinValue { get; }
            public Int32? MaxValue { get; }
        }

        public class ParseInfoBase
        {
            public ParseInfoBase(string[] lineArray)
            {
                LineArray = lineArray;
            }

            public string[] LineArray { get; }
        }

        public class ParseInfoMwb : ParseInfoBase
        {
            public ParseInfoMwb(UInt32 serviceId, UInt32 dataTypeId, string[] lineArray, string[] nameArray, string[] nameDetailArray,
                double? scaleOffset, double? scaleMult, double? scaleDiv, string unitText, UInt32? byteOffset, UInt32? bitOffset, UInt32? bitLength, List<ValueName> nameValueList) : base(lineArray)
            {
                ServiceId = serviceId;
                DataTypeId = dataTypeId;
                NameArray = nameArray;
                NameDetailArray = nameDetailArray;
                ScaleOffset = scaleOffset;
                ScaleMult = scaleMult;
                ScaleDiv = scaleDiv;
                UnitText = unitText;
                ByteOffset = byteOffset;
                BitOffset = bitOffset;
                BitLength = bitLength;
                NameValueList = nameValueList;
            }

            public UInt32 ServiceId { get; }
            public UInt32 DataTypeId { get; }
            public string[] NameArray { get; }
            public string[] NameDetailArray { get; }
            public double? ScaleOffset { get; }
            public double? ScaleMult { get; }
            public double? ScaleDiv { get; }
            public string UnitText { get; }
            public UInt32? ByteOffset { get; }
            public UInt32? BitOffset { get; }
            public UInt32? BitLength { get; }
            public List<ValueName> NameValueList { get; }

            public static string DataTypeIdToString(UInt32 dataTypeId)
            {
                UInt32 dataTypeEnum = dataTypeId & DataTypeMaskEnum;
                string dataTypeName = Enum.GetName(typeof(DataType), dataTypeEnum);
                if (dataTypeName == null)
                {
                    dataTypeName = string.Format(CultureInfo.InvariantCulture, "{0}", dataTypeEnum);
                }

                if ((dataTypeId & DataTypeMaskSwapped) != 0x00)
                {
                    dataTypeName += " (Swapped)";
                }
                if ((dataTypeId & DataTypeMaskUnit) != 0x00)
                {
                    dataTypeName += " (Unit)";
                }

                return dataTypeName;
            }
        }

        private class SegmentInfo
        {
            public SegmentInfo(SegmentType segmentType, string segmentName, string fileName)
            {
                SegmentType = segmentType;
                SegmentName = segmentName;
                FileName = fileName;
            }

            public SegmentType SegmentType { get; }
            public string SegmentName { get; }
            public string FileName { get; }
            public List<string[]> LineList { set; get; }
        }

        private readonly SegmentInfo[] _segmentInfos =
        {
            new SegmentInfo(SegmentType.Adp, "ADP", "RA"),
            new SegmentInfo(SegmentType.Dtc, "DTC", "RD"),
            new SegmentInfo(SegmentType.Ffmux, "FFMUX", "RF"),
            new SegmentInfo(SegmentType.Ges, "GES", "RG"),
            new SegmentInfo(SegmentType.Mwb, "MWB", "RM"),
            new SegmentInfo(SegmentType.Sot, "SOT", "RS"),
            new SegmentInfo(SegmentType.Xpl, "XPL", "RX"),
        };

        private Dictionary<string, string> _redirMap;
        private Dictionary<UInt32, string[]> _textMap;
        private Dictionary<UInt32, string[]> _unitMap;
        private ILookup<UInt32, string[]> _ttdopLookup;

        public bool Init(string dirName)
        {
            try
            {
                List<string[]> redirList = ExtractFileSegment(new List<string> {Path.Combine(dirName, "ReDir" + FileExtension)}, "DIR");
                if (redirList == null)
                {
                    return false;
                }

                _redirMap = new Dictionary<string, string>();
                foreach (string[] redirArray in redirList)
                {
                    if (redirArray.Length != 3)
                    {
                        return false;
                    }
                    _redirMap.Add(redirArray[1].ToUpperInvariant(), redirArray[2]);
                }

                _textMap = CreateTextDict(dirName, "TTText*" + FileExtension, "TXT");
                if (_textMap == null)
                {
                    return false;
                }

                _unitMap = CreateTextDict(dirName, "Unit*" + FileExtension, "UNT");
                if (_unitMap == null)
                {
                    return false;
                }

                List<string[]> ttdopList = ExtractFileSegment(new List<string> { Path.Combine(dirName, "TTDOP" + FileExtension) }, "DOP");
                if (ttdopList == null)
                {
                    return false;
                }

                _ttdopLookup = ttdopList.ToLookup(item => UInt32.Parse(item[0]));

                foreach (SegmentInfo segmentInfo in _segmentInfos)
                {
                    string fileName = Path.Combine(dirName, Path.ChangeExtension(segmentInfo.FileName, FileExtension));
                    List<string[]> lineList = ExtractFileSegment(new List<string> {fileName}, segmentInfo.SegmentName);
                    if (lineList == null)
                    {
                        return false;
                    }

                    segmentInfo.LineList = lineList;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public List<ParseInfoBase> ExtractFileSegment(List<string> fileList, SegmentType segmentType)
        {
            SegmentInfo segmentInfoSel = null;
            foreach (SegmentInfo segmentInfo in _segmentInfos)
            {
                if (segmentInfo.SegmentType == segmentType)
                {
                    segmentInfoSel = segmentInfo;
                    break;
                }
            }

            if (segmentInfoSel?.LineList == null)
            {
                return null;
            }

            List<string[]> lineList = ExtractFileSegment(fileList, segmentInfoSel.SegmentName);
            if (lineList == null)
            {
                return null;
            }

            List<ParseInfoBase> resultList = new List<ParseInfoBase>();
            foreach (string[] line in lineList)
            {
                if (line.Length < 2)
                {
                    return null;
                }

                if (!UInt32.TryParse(line[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 value))
                {
                    return null;
                }

                if (value < 1 || value > segmentInfoSel.LineList.Count)
                {
                    return null;
                }

                string[] lineArray = segmentInfoSel.LineList[(int) value - 1];

                ParseInfoBase parseInfo;
                switch (segmentType)
                {
                    case SegmentType.Mwb:
                    {
                        if (lineArray.Length < 14)
                        {
                            return null;
                        }
                        if (!UInt32.TryParse(lineArray[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 nameKey))
                        {
                            return null;
                        }

                        if (!_textMap.TryGetValue(nameKey, out string[] nameArray))
                        {
                            return null;
                        }

                        if (!UInt32.TryParse(lineArray[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 serviceId))
                        {
                            return null;
                        }

                        if (!UInt32.TryParse(lineArray[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 dataTypeId))
                        {
                            return null;
                        }
                        DataType dataType = (DataType) (dataTypeId & DataTypeMaskEnum);

                        UInt32? dataTypeExtra = null;
                        if (UInt32.TryParse(lineArray[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 dataTypeE))
                        {
                            dataTypeExtra = dataTypeE;
                        }

                        UInt32? byteOffset = null;
                        if (UInt32.TryParse(lineArray[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 byteO))
                        {
                            byteOffset = byteO;
                        }

                        UInt32? bitOffset = null;
                        if (UInt32.TryParse(lineArray[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 bitO))
                        {
                            bitOffset = bitO;
                        }

                        UInt32? bitCount = null;
                        if (UInt32.TryParse(lineArray[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 bitC))
                        {
                            bitCount = bitC;
                        }

                        string[] nameDetailArray = null;
                        if (UInt32.TryParse(lineArray[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 nameDetailKey))
                        {
                            if (!_textMap.TryGetValue(nameDetailKey, out nameDetailArray))
                            {
                                return null;
                            }
                        }

                        double? scaleOffset = null;
                        double? scaleMult = null;
                        double? scaleDiv = null;
                        string unitText = null;
                        List<ValueName> nameValueList = null;
                        switch (dataType)
                        {
                            case DataType.ValueName:
                            {
                                nameValueList = new List<ValueName>();
                                IEnumerable<string[]> bitList = _ttdopLookup[dataTypeExtra.Value];
                                if (bitList != null)
                                {
                                    foreach (string[] ttdopArray in bitList)
                                    {
                                        if (ttdopArray.Length >= 5)
                                        {
                                            nameValueList.Add(new ValueName(this, ttdopArray));
                                        }
                                    }
                                }
                                break;
                            }
                        }

                        if (double.TryParse(lineArray[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double scaleO))
                        {
                            scaleOffset = scaleO;
                        }

                        if (double.TryParse(lineArray[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double scaleM))
                        {
                            scaleMult = scaleM;
                        }

                        if (double.TryParse(lineArray[6], NumberStyles.Float, CultureInfo.InvariantCulture, out double scaleD))
                        {
                            scaleDiv = scaleD;
                        }

                        if ((dataTypeId & DataTypeMaskUnit) != 0x00)
                        {
                            if (UInt32.TryParse(lineArray[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 unitKey))
                            {
                                if (!_unitMap.TryGetValue(unitKey, out string[] unitArray))
                                {
                                    return null;
                                }
                                if (unitArray.Length < 1)
                                {
                                    return null;
                                }
                                unitText = unitArray[0];
                            }
                        }

                        parseInfo = new ParseInfoMwb(serviceId, dataTypeId, lineArray, nameArray, nameDetailArray, scaleOffset, scaleMult, scaleDiv, unitText,
                            byteOffset, bitOffset, bitCount, nameValueList);
                        break;
                    }

                    default:
                        parseInfo = new ParseInfoBase(lineArray);
                        break;
                }
                resultList.Add(parseInfo);
            }

            return resultList;
        }

        public static Dictionary<uint, string[]> CreateTextDict(string dirName, string fileSpec, string segmentName)
        {
            try
            {
                string[] files = Directory.GetFiles(dirName, fileSpec, SearchOption.TopDirectoryOnly);
                if (files.Length != 1)
                {
                    return null;
                }
                List<string[]> textList = ExtractFileSegment(files.ToList(), segmentName);
                if (textList == null)
                {
                    return null;
                }

                Dictionary<uint, string[]> dict = new Dictionary<uint, string[]>();
                foreach (string[] textArray in textList)
                {
                    if (textArray.Length < 2)
                    {
                        return null;
                    }
                    if (!UInt32.TryParse(textArray[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 key))
                    {
                        return null;
                    }

                    dict.Add(key, textArray.Skip(1).ToArray());
                }

                return dict;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static List<string[]> ExtractFileSegment(List<string> fileList, string segmentName)
        {
            string segmentStart = "[" + segmentName + "]";
            string segmentEnd = "[/" + segmentName + "]";

            List<string[]> lineList = new List<string[]>();
            foreach (string fileName in fileList)
            {
                try
                {
                    using (StreamReader sr = new StreamReader(fileName, Encoding))
                    {
                        bool inSegment = false;
                        for (;;)
                        {
                            string line = sr.ReadLine();
                            if (line == null)
                            {
                                break;
                            }

                            if (line.StartsWith("["))
                            {
                                if (string.Compare(line, segmentStart, StringComparison.OrdinalIgnoreCase) == 0)
                                {
                                    inSegment = true;
                                }
                                else if (string.Compare(line, segmentEnd, StringComparison.OrdinalIgnoreCase) == 0)
                                {
                                    inSegment = false;
                                }
                                continue;
                            }

                            if (!inSegment)
                            {
                                continue;
                            }
                            string[] lineArray = line.Split(',');
                            if (lineArray.Length > 0)
                            {
                                lineList.Add(lineArray);
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            }
            return lineList;
        }

        public List<string> GetFileList(string fileName)
        {
            string dirName = Path.GetDirectoryName(fileName);
            if (dirName == null)
            {
                return null;
            }
            string fullName = Path.ChangeExtension(fileName, FileExtension);
            if (!File.Exists(fullName))
            {
                string key = Path.GetFileNameWithoutExtension(fileName)?.ToUpperInvariant();
                if (key == null)
                {
                    return null;
                }

                if (!_redirMap.TryGetValue(key, out string mappedName))
                {
                    return null;
                }

                if (string.Compare(mappedName, "EMPTY", StringComparison.OrdinalIgnoreCase) == 0)
                {   // no entry
                    return null;
                }

                fullName = Path.ChangeExtension(mappedName, FileExtension);
                if (fullName == null)
                {
                    return null;
                }
                fullName = Path.Combine(dirName, fullName);

                if (!File.Exists(fullName))
                {
                    return null;
                }
            }

            List<string> includeFiles = new List<string> {fullName};
            if (!GetIncludeFiles(fullName, includeFiles))
            {
                return null;
            }

            return includeFiles;
        }

        public static bool GetIncludeFiles(string fileName, List<string> includeFiles)
        {
            try
            {
                if (!File.Exists(fileName))
                {
                    return false;
                }

                string dir = Path.GetDirectoryName(fileName);
                if (dir == null)
                {
                    return false;
                }

                List<string[]> lineList = ExtractFileSegment(new List<string> { fileName }, "INC");
                if (lineList == null)
                {
                    return false;
                }

                foreach (string[] line in lineList)
                {
                    if (line.Length >= 2)
                    {
                        string file = line[1];
                        if (!string.IsNullOrWhiteSpace(file))
                        {
                            string fileNameInc = Path.Combine(dir, Path.ChangeExtension(file, FileExtension));
                            if (File.Exists(fileNameInc) && !includeFiles.Contains(fileNameInc))
                            {
                                includeFiles.Add(fileNameInc);
                                if (!GetIncludeFiles(fileNameInc, includeFiles))
                                {
                                    return false;
                                }
                            }
                        }
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}