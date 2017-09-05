﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using EdiabasLib;

// ReSharper disable CanBeReplacedWithTryCastAndCheckForNull

namespace BmwDeepObd
{
    public class EdiabasThread : IDisposable
    {
        public class EdiabasErrorReport
        {
            public EdiabasErrorReport(string ecuName, Dictionary<string, EdiabasNet.ResultData> errorDict, List<Dictionary<string, EdiabasNet.ResultData>> errorDetailSet) :
                this(ecuName, errorDict, errorDetailSet, string.Empty)
            {
            }

            public EdiabasErrorReport(string ecuName, Dictionary<string, EdiabasNet.ResultData> errorDict, List<Dictionary<string, EdiabasNet.ResultData>> errorDetailSet, string execptionText)
            {
                EcuName = ecuName;
                ErrorDict = errorDict;
                ErrorDetailSet = errorDetailSet;
                ExecptionText = execptionText;
            }

            public string EcuName { get; }

            public Dictionary<string, EdiabasNet.ResultData> ErrorDict { get; }

            public List<Dictionary<string, EdiabasNet.ResultData>> ErrorDetailSet { get; }

            public string ExecptionText { get; }
        }

        public delegate void DataUpdatedEventHandler(object sender, EventArgs e);
        public event DataUpdatedEventHandler DataUpdated;
        public delegate void ThreadTerminatedEventHandler(object sender, EventArgs e);
        public event ThreadTerminatedEventHandler ThreadTerminated;

        public JobReader.PageInfo JobPageInfo
        {
            get;
            set;
        }

        public bool CommActive
        {
            get;
            set;
        }

        public bool Connected
        {
            get;
            private set;
        }

        public List<EdiabasErrorReport> EdiabasErrorReportList
        {
            get;
            private set;
        }

        public MultiMap<string, EdiabasNet.ResultData> EdiabasResultDict
        {
            get;
            private set;
        }

        public string EdiabasErrorMessage
        {
            get;
            private set;
        }

        public int UpdateProgress
        {
            get;
            private set;
        }

        public JobReader.PageInfo ResultPageInfo
        {
            get;
            private set;
        }

        public List<string> ErrorResetList { get; set; }

        public EdiabasNet Ediabas { get; private set; }

        public static readonly Object DataLock = new Object();
        private static readonly long TickResolMs = Stopwatch.Frequency / 1000;
        private const char DataLogSeparator = '\t';

        private bool _disposed;
        private volatile bool _stopThread;
        private bool _threadRunning;
        private Thread _workerThread;
        private bool _ediabasInitReq;
        private bool _ediabasJobAbort;
        private JobReader.PageInfo _lastPageInfo;
        private long _lastUpdateTime;
        private string _logDir;
        private bool _appendLog;
        private StreamWriter _swDataLog;


        public EdiabasThread(string ecuPath, ActivityCommon activityCommon)
        {
            _stopThread = false;
            _threadRunning = false;
            _workerThread = null;
            Ediabas = new EdiabasNet
            {
                EdInterfaceClass = activityCommon.GetEdiabasInterfaceClass(),
                AbortJobFunc = AbortEdiabasJob
            };
            Ediabas.SetConfigProperty("EcuPath", ecuPath);

            InitProperties(null);
        }

        public void Dispose()
        {
            Dispose(true);
            // This object will be cleaned up by the Dispose method.
            // Therefore, you should call GC.SupressFinalize to
            // take this object off the finalization queue
            // and prevent finalization code for this object
            // from executing a second time.
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            // Check to see if Dispose has already been called.
            if (!_disposed)
            {
                // If disposing equals true, dispose all managed
                // and unmanaged resources.
                if (disposing)
                {
                    // Dispose managed resources.
                    Ediabas.Dispose();
                    Ediabas = null;
                    CloseDataLog();
                }

                // Note disposing has been done.
                _disposed = true;
            }
        }

        public bool StartThread(string comPort, object connectParameter, JobReader.PageInfo pageInfo, bool commActive, string traceDir, bool traceAppend, string logDir, bool appendLog)
        {
            if (_workerThread != null)
            {
                return false;
            }
            try
            {
                _stopThread = false;
                if (Ediabas.EdInterfaceClass is EdInterfaceObd)
                {
                    ((EdInterfaceObd)Ediabas.EdInterfaceClass).UdsDtcStatusOverride = ActivityCommon.UdsDtcStatusOverride;
                    ((EdInterfaceObd)Ediabas.EdInterfaceClass).ComPort = comPort;
                }
                else if (Ediabas.EdInterfaceClass is EdInterfaceEnet)
                {
                    if (!string.IsNullOrEmpty(comPort))
                    {
                        ((EdInterfaceEnet)Ediabas.EdInterfaceClass).RemoteHost = comPort;
                    }
                }
                Ediabas.EdInterfaceClass.ConnectParameter = connectParameter;
                if (!string.IsNullOrEmpty(traceDir))
                {
                    Ediabas.SetConfigProperty("TracePath", traceDir);
                    Ediabas.SetConfigProperty("IfhTrace", string.Format("{0}", (int)EdiabasNet.EdLogLevel.Error));
                    Ediabas.SetConfigProperty("AppendTrace", traceAppend ? "1" : "0");
                    Ediabas.SetConfigProperty("CompressTrace", "1");
                }
                else
                {
                    Ediabas.SetConfigProperty("IfhTrace", "0");
                }
                CommActive = commActive;
                JobPageInfo = pageInfo;
                _lastPageInfo = null;
                _lastUpdateTime = Stopwatch.GetTimestamp();
                _logDir = logDir;
                _appendLog = appendLog;
                InitProperties(pageInfo);
                _workerThread = new Thread(ThreadFunc);
                _threadRunning = true;
                _workerThread.Start();
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }

        public void StopThread(bool wait)
        {
            if (_workerThread != null)
            {
                _stopThread = true;
                if (wait)
                {
                    _workerThread.Join();
                    _workerThread = null;
                }
            }
        }

        public bool ThreadRunning()
        {
            if (_workerThread == null) return false;
            return _threadRunning;
        }

        public bool ThreadStopping()
        {
            if (_workerThread == null) return false;
            return _stopThread;
        }

        // ReSharper disable once UnusedMethodReturnValue.Local
        private bool OpenDataLog(JobReader.PageInfo pageInfo)
        {
            if (_swDataLog == null && pageInfo != null && !string.IsNullOrEmpty(_logDir) && !string.IsNullOrEmpty(pageInfo.LogFile))
            {
                try
                {
                    FileMode fileMode;
                    string logFileName = pageInfo.LogFile;
                    logFileName = logFileName.Replace("{D}", DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture));

                    string fileName = Path.Combine(_logDir, logFileName);
                    if (File.Exists(fileName))
                    {
                        fileMode = (_appendLog || ActivityCommon.JobReader.AppendLog) ? FileMode.Append : FileMode.Create;
                    }
                    else
                    {
                        fileMode = FileMode.Create;
                    }
                    _swDataLog = new StreamWriter(new FileStream(fileName, fileMode, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8);
                    if (fileMode == FileMode.Create)
                    {
                        // add header
                        StringBuilder sbLog = new StringBuilder();
                        //sbLog.Append(GetString(Resource.String.datalog_date));
                        foreach (JobReader.DisplayInfo displayInfo in pageInfo.DisplayList)
                        {
                            if (!string.IsNullOrEmpty(displayInfo.LogTag))
                            {
                                sbLog.Append(DataLogSeparator);
                                sbLog.Append(displayInfo.LogTag.Replace(DataLogSeparator, ' '));
                            }
                        }
                        try
                        {
                            sbLog.Append("\r\n");
                            _swDataLog.Write(sbLog.ToString());
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    }
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
            return false;
        }

        private void CloseDataLog()
        {
            if (_swDataLog != null)
            {
                _swDataLog.Dispose();
                _swDataLog = null;
            }
        }

        private void LogData(JobReader.PageInfo pageInfo, MultiMap<string, EdiabasNet.ResultData> resultDict)
        {
            if (_swDataLog == null)
            {
                return;
            }
            StringBuilder sbLog = new StringBuilder();
            string currDateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            sbLog.Append(currDateTime);
            bool logDataPresent = false;
            foreach (JobReader.DisplayInfo displayInfo in pageInfo.DisplayList)
            {
                string result = ActivityCommon.FormatResult(pageInfo, displayInfo, resultDict, out Android.Graphics.Color? _);
                if (result != null)
                {
                    if (!string.IsNullOrEmpty(displayInfo.LogTag))
                    {
                        if (!string.IsNullOrWhiteSpace(result))
                        {
                            logDataPresent = true;
                        }
                        sbLog.Append(DataLogSeparator);
                        sbLog.Append(result.Replace(DataLogSeparator, ' '));
                    }
                }
            }
            if (logDataPresent)
            {
                try
                {
                    sbLog.Append("\r\n");
                    _swDataLog.Write(sbLog.ToString());
                }
                catch (Exception)
                {
                    // ignored
                }
            }
        }

        private void ThreadFunc()
        {
            DataUpdatedEvent();
            _lastPageInfo = null;
            while (!_stopThread)
            {
                try
                {
                    if (!CommActive)
                    {
                        continue;
                    }
                    JobReader.PageInfo copyPageInfo = JobPageInfo;

                    if (_lastPageInfo != copyPageInfo)
                    {
                        _lastPageInfo = copyPageInfo;
                        InitProperties(copyPageInfo, true);
                    }

                    bool result = CommEdiabas(copyPageInfo);

                    if (result)
                    {
                        Connected = true;
                    }
                }
                catch (Exception)
                {
                    break;
                }
                DataUpdatedEvent();
            }
            _threadRunning = false;
            DataUpdatedEvent();
            ThreadTerminatedEvent();
        }

        private bool CommEdiabas(JobReader.PageInfo pageInfo)
        {
            if (pageInfo == null)
            {
                lock (DataLock)
                {
                    EdiabasResultDict = null;
                    EdiabasErrorReportList = null;
                    EdiabasErrorMessage = "No Page info";
                    ResultPageInfo = null;
                    UpdateProgress = 0;
                }
                return false;
            }

            if (pageInfo.ErrorsInfo != null)
            {   // read errors
                if (_ediabasInitReq)
                {
                    lock (DataLock)
                    {
                        EdiabasErrorReportList = null;
                    }
                    _ediabasJobAbort = false;
                    _ediabasInitReq = false;
                }
                List<string> errorResetList;
                lock (DataLock)
                {
                    errorResetList = ErrorResetList;
                    ErrorResetList = null;
                }

                List<EdiabasErrorReport> errorReportList = new List<EdiabasErrorReport>();

                int index = -1;
                foreach (JobReader.EcuInfo ecuInfo in pageInfo.ErrorsInfo.EcuList)
                {
                    index++;
                    if (_lastPageInfo != JobPageInfo)
                    {   // page change
                        break;
                    }
                    if (_ediabasJobAbort)
                    {
                        break;
                    }
                    try
                    {
                        Ediabas.ResolveSgbdFile(ecuInfo.Sgbd);
                    }
                    catch (Exception ex)
                    {
                        string exText = String.Empty;
                        if (!AbortEdiabasJob())
                        {
                            exText = EdiabasNet.GetExceptionText(ex);
                        }
                        errorReportList.Add(new EdiabasErrorReport(ecuInfo.Name, null, null, exText));
                        continue;
                    }

                    try
                    {
                        try
                        {
                            if (errorResetList != null && errorResetList.Any(ecu => string.CompareOrdinal(ecu, ecuInfo.Name) == 0))
                            {   // error reset requested
                                Ediabas.ArgString = string.Empty;
                                Ediabas.ArgBinaryStd = null;
                                Ediabas.ResultsRequests = string.Empty;
                                Ediabas.ExecuteJob(ActivityCommon.SelectedManufacturer == ActivityCommon.ManufacturerType.Bmw ? "FS_LOESCHEN" : "Fehlerspeicher_loeschen");
                            }
                        }
                        catch (Exception)
                        {
                            // ignored
                        }

                        Ediabas.ArgString = "ALL";
                        Ediabas.ArgBinaryStd = null;
                        Ediabas.ResultsRequests = string.Empty;
                        Ediabas.NoInitForVJobs = true;
                        Ediabas.ExecuteJob("_JOBS");    // force to load file

                        string errorJob = ActivityCommon.SelectedManufacturer == ActivityCommon.ManufacturerType.Bmw ? "FS_LESEN" : "Fehlerspeicher_abfragen";
                        if (Ediabas.IsJobExisting(errorJob))
                        {
                            Ediabas.ArgString = string.Empty;
                            Ediabas.ArgBinaryStd = null;
                            Ediabas.ResultsRequests = string.Empty;
                            Ediabas.ExecuteJob(errorJob);

                            List<Dictionary<string, EdiabasNet.ResultData>> resultSets = new List<Dictionary<string, EdiabasNet.ResultData>>(Ediabas.ResultSets);

                            bool jobOk = false;
                            bool saeMode = false;
                            if (ActivityCommon.SelectedManufacturer != ActivityCommon.ManufacturerType.Bmw)
                            {
                                if (resultSets.Count > 0)
                                {
                                    if (resultSets[0].TryGetValue("JOBSTATUS", out EdiabasNet.ResultData resultData))
                                    {
                                        if (resultData.OpData is string)
                                        {
                                            // read details
                                            string jobStatus = (string) resultData.OpData;
                                            if (String.Compare(jobStatus, "OKAY", StringComparison.OrdinalIgnoreCase) == 0)
                                            {
                                                jobOk = true;
                                            }
                                        }
                                    }
                                }
                                if (!jobOk)
                                {
                                    if (Ediabas.IsJobExisting("FehlerspeicherSAE_abfragen"))
                                    {
                                        Ediabas.ArgString = string.Empty;
                                        Ediabas.ArgBinaryStd = null;
                                        Ediabas.ResultsRequests = string.Empty;
                                        Ediabas.ExecuteJob("FehlerspeicherSAE_abfragen");
                                        resultSets = new List<Dictionary<string, EdiabasNet.ResultData>>(Ediabas.ResultSets);
                                        if (resultSets.Count > 0)
                                        {
                                            if (resultSets[0].TryGetValue("JOBSTATUS", out EdiabasNet.ResultData resultData))
                                            {
                                                if (resultData.OpData is string)
                                                {
                                                    // read details
                                                    string jobStatus = (string) resultData.OpData;
                                                    if (String.Compare(jobStatus, "OKAY", StringComparison.OrdinalIgnoreCase) == 0)
                                                    {
                                                        jobOk = true;
                                                        saeMode = true;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        jobOk = true;
                                    }
                                }
                            }
                            else
                            {
                                if (resultSets.Count > 1)
                                {
                                    if (resultSets[resultSets.Count - 1].TryGetValue("JOB_STATUS", out EdiabasNet.ResultData resultData))
                                    {
                                        if (resultData.OpData is string)
                                        {
                                            // read details
                                            string jobStatus = (string) resultData.OpData;
                                            if (String.Compare(jobStatus, "OKAY", StringComparison.OrdinalIgnoreCase) == 0)
                                            {
                                                jobOk = true;
                                            }
                                        }
                                    }
                                }
                            }

                            if (jobOk)
                            {
                                Dictionary<string, EdiabasNet.ResultData> resultDict0 = null;
                                int dictIndex = 0;
                                foreach (Dictionary<string, EdiabasNet.ResultData> resultDictLocal in resultSets)
                                {
                                    EdiabasNet.ResultData resultData;
                                    if (dictIndex == 0)
                                    {
                                        resultDict0 = resultDictLocal;
                                        dictIndex++;
                                        continue;
                                    }

                                    if (ActivityCommon.SelectedManufacturer != ActivityCommon.ManufacturerType.Bmw)
                                    {
                                        if (resultDictLocal.TryGetValue("FNR_WERT", out resultData))
                                        {
                                            if (resultData.OpData is Int64)
                                            {
                                                Dictionary<string, EdiabasNet.ResultData> resultDictTemp = null;
                                                MergeResultDictionarys(ref resultDictTemp, resultDictLocal);
                                                MergeResultDictionarys(ref resultDictTemp, resultDict0);
                                                resultDictTemp.Add("SAE", new EdiabasNet.ResultData(EdiabasNet.ResultType.TypeI, "SAE", (Int64) (saeMode ? 1 : 0)));
                                                errorReportList.Add(new EdiabasErrorReport(ecuInfo.Name, resultDictTemp, null));
                                            }
                                        }
                                        dictIndex++;
                                        continue;
                                    }
                                    if (resultDictLocal.TryGetValue("F_ORT_NR", out resultData))
                                    {
                                        if (resultData.OpData is Int64)
                                        {
                                            // read details
                                            Ediabas.ArgString = string.Format("0x{0:X02}", (Int64) resultData.OpData);
                                            Ediabas.ArgBinaryStd = null;
                                            Ediabas.ResultsRequests = ecuInfo.Results;

                                            bool details;
                                            try
                                            {
                                                Ediabas.ExecuteJob("FS_LESEN_DETAIL");
                                                details = true;
                                            }
                                            catch (Exception)
                                            {
                                                // no details
                                                details = false;
                                            }

                                            if (details)
                                            {
                                                List<Dictionary<string, EdiabasNet.ResultData>> resultSetsDetail = new List<Dictionary<string, EdiabasNet.ResultData>>(Ediabas.ResultSets);
                                                errorReportList.Add(new EdiabasErrorReport(ecuInfo.Name, resultDictLocal, new List<Dictionary<string, EdiabasNet.ResultData>>(resultSetsDetail)));
                                            }
                                            else
                                            {
                                                errorReportList.Add(new EdiabasErrorReport(ecuInfo.Name, resultDictLocal, null));
                                            }
                                        }
                                    }
                                    dictIndex++;
                                }
                            }
                            else
                            {
                                errorReportList.Add(new EdiabasErrorReport(ecuInfo.Name, null, null));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        string exText = String.Empty;
                        if (!AbortEdiabasJob())
                        {
                            exText = EdiabasNet.GetExceptionText(ex);
                        }
                        errorReportList.Add(new EdiabasErrorReport(ecuInfo.Name, null, null, exText));
                        continue;
                    }
                    if (EdiabasErrorReportList == null)
                    {   // first update
                        lock (DataLock)
                        {
                            UpdateProgress = index * 100 / pageInfo.ErrorsInfo.EcuList.Count;
                        }
                        DataUpdatedEvent();
                    }
                }

                lock (DataLock)
                {
                    EdiabasResultDict = null;
                    EdiabasErrorReportList = errorReportList;
                    EdiabasErrorMessage = string.Empty;
                    ResultPageInfo = pageInfo;
                    UpdateProgress = 100;
                }
                return true;
            }
            // execute jobs

            bool firstRequestCall = false;
            string currentSgbd = string.Empty;
            if (_ediabasInitReq)
            {
                firstRequestCall = true;
                _ediabasJobAbort = false;

                if (!string.IsNullOrEmpty(pageInfo.JobsInfo?.Sgbd))
                {
                    try
                    {
                        Ediabas.ResolveSgbdFile(pageInfo.JobsInfo.Sgbd);
                    }
                    catch (Exception ex)
                    {
                        string exText = String.Empty;
                        if (!AbortEdiabasJob())
                        {
                            exText = EdiabasNet.GetExceptionText(ex);
                        }
                        lock (DataLock)
                        {
                            EdiabasResultDict = null;
                            EdiabasErrorReportList = null;
                            EdiabasErrorMessage = exText;
                            ResultPageInfo = pageInfo;
                            UpdateProgress = 0;
                        }
                        return false;
                    }
                    currentSgbd = pageInfo.JobsInfo.Sgbd;
                }

                _ediabasInitReq = false;
            }

            MultiMap<string, EdiabasNet.ResultData> resultDict = null;
            try
            {
                if ((pageInfo.JobsInfo != null) && (pageInfo.JobsInfo.JobList.Count > 0))
                {
                    foreach (JobReader.JobInfo jobInfo in pageInfo.JobsInfo.JobList)
                    {
                        if (!string.IsNullOrEmpty(jobInfo.Name))
                        {
                            string sgbd = string.Empty;
                            if (!string.IsNullOrEmpty(jobInfo.Sgbd))
                            {
                                sgbd = jobInfo.Sgbd;
                            }
                            else if (!string.IsNullOrEmpty(pageInfo.JobsInfo.Sgbd))
                            {
                                sgbd = pageInfo.JobsInfo.Sgbd;
                            }

                            if (!string.IsNullOrEmpty(sgbd) &&
                                string.Compare(currentSgbd, sgbd, StringComparison.OrdinalIgnoreCase) != 0)
                            {
                                Ediabas.ResolveSgbdFile(sgbd);
                                currentSgbd = sgbd;
                            }

                            if (firstRequestCall && !string.IsNullOrEmpty(jobInfo.ArgsFirst))
                            {
                                Ediabas.ArgString = jobInfo.ArgsFirst;
                            }
                            else
                            {
                                Ediabas.ArgString = jobInfo.Args;
                            }
                            Ediabas.ArgBinaryStd = null;
                            Ediabas.ResultsRequests = jobInfo.Results;
                            Ediabas.ExecuteJob(jobInfo.Name);

                            List<Dictionary<string, EdiabasNet.ResultData>> resultSets = Ediabas.ResultSets;
                            if (resultSets != null && resultSets.Count >= 2)
                            {
                                int dictIndex = 0;
                                foreach (Dictionary<string, EdiabasNet.ResultData> resultDictLocal in resultSets)
                                {
                                    if (dictIndex == 0)
                                    {
                                        dictIndex++;
                                        continue;
                                    }
                                    if (string.IsNullOrEmpty(jobInfo.Id))
                                    {
                                        MergeResultDictionarys(ref resultDict, resultDictLocal, jobInfo.Name + "#");
                                    }
                                    else
                                    {
                                        MergeResultDictionarys(ref resultDict, resultDictLocal, jobInfo.Id + "#" + dictIndex + "#");
                                    }
                                    dictIndex++;
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (pageInfo.ClassObject != null)
                    {
                        bool executeJob = false;
                        bool executeJobMulti = false;
                        Type pageType = pageInfo.ClassObject.GetType();
                        MethodInfo methodInfoJob = pageType.GetMethod("ExecuteJob");
                        if (methodInfoJob != null)
                        {
                            ParameterInfo[] parInfo = methodInfoJob.GetParameters();
                            if (parInfo.Length == 3)
                            {
                                if (parInfo[0].ParameterType == typeof(EdiabasNet) && parInfo[2].ParameterType == typeof(bool))
                                {
                                    if (parInfo[1].ParameterType == typeof(Dictionary<string, EdiabasNet.ResultData>).MakeByRefType())
                                    {
                                        executeJob = true;
                                    }
                                    if (parInfo[1].ParameterType == typeof(MultiMap<string, EdiabasNet.ResultData>).MakeByRefType())
                                    {
                                        executeJobMulti = true;
                                    }
                                }
                            }
                            if (executeJobMulti)
                            {
                                object[] args = { Ediabas, null, firstRequestCall };
                                methodInfoJob.Invoke(pageInfo.ClassObject, args);
                                resultDict = args[1] as MultiMap<string, EdiabasNet.ResultData>;
                                //pageInfo.ClassObject.ExecuteJob(Ediabas, ref resultDict, firstRequestCall);
                            }
                            else if (executeJob)
                            {
                                object[] args = { Ediabas, null, firstRequestCall };
                                methodInfoJob.Invoke(pageInfo.ClassObject, args);
                                // ReSharper disable once UsePatternMatching
                                Dictionary<string, EdiabasNet.ResultData> resultDictLocal = args[1] as Dictionary<string, EdiabasNet.ResultData>;
                                //pageInfo.ClassObject.ExecuteJob(Ediabas, ref resultDictLocal, firstRequestCall);
                                if (resultDictLocal != null)
                                {
                                    MergeResultDictionarys(ref resultDict, resultDictLocal);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _ediabasInitReq = true;
                string exText = String.Empty;
                if (!AbortEdiabasJob())
                {
                    exText = EdiabasNet.GetExceptionText(ex);
                }
                lock (DataLock)
                {
                    EdiabasResultDict = null;
                    EdiabasErrorReportList = null;
                    EdiabasErrorMessage = exText;
                    ResultPageInfo = pageInfo;
                    UpdateProgress = 0;
                }
                return false;
            }

            LogData(pageInfo, resultDict);

            lock (DataLock)
            {
                EdiabasResultDict = resultDict;
                EdiabasErrorReportList = null;
                EdiabasErrorMessage = string.Empty;
                ResultPageInfo = pageInfo;
                UpdateProgress = 0;
            }
            return true;
        }

        public static void MergeResultDictionarys(ref Dictionary<string, EdiabasNet.ResultData> resultDict, Dictionary<string, EdiabasNet.ResultData> mergeDict, string prefix = null)
        {
            if (resultDict == null)
            {
                resultDict = new Dictionary<string, EdiabasNet.ResultData>();
            }

            foreach (string key in mergeDict.Keys)
            {
                string newKey = key;
                if (prefix != null)
                {
                    newKey = (prefix + key).ToUpperInvariant();
                }
                if (!resultDict.ContainsKey(newKey))
                {
                    resultDict.Add(newKey, mergeDict[key]);
                }
            }
        }

        public static void MergeResultDictionarys(ref MultiMap<string, EdiabasNet.ResultData> resultDict, Dictionary<string, EdiabasNet.ResultData> mergeDict, string prefix = null)
        {
            if (resultDict == null)
            {
                resultDict = new MultiMap<string,EdiabasNet.ResultData>();
            }

            foreach (string key in mergeDict.Keys)
            {
                string newKey = key;
                if (prefix != null)
                {
                    newKey = (prefix + key).ToUpperInvariant();
                }
                resultDict.Add(newKey, mergeDict[key]);
            }
        }

        private bool AbortEdiabasJob()
        {
            if (_ediabasJobAbort || _stopThread)
            {
                return true;
            }
            return false;
        }

        private void InitProperties(JobReader.PageInfo pageInfo, bool deviceChange = false)
        {
            if (!deviceChange)
            {
                Connected = false;
            }
            CloseDataLog();
            EdiabasResultDict = null;
            EdiabasErrorMessage = string.Empty;
            EdiabasErrorReportList = null;
            ErrorResetList = null;
            ResultPageInfo = null;
            UpdateProgress = 0;

            _ediabasInitReq = true;
            _ediabasJobAbort = deviceChange;
            OpenDataLog(pageInfo);
        }

        private void DataUpdatedEvent()
        {
            while (Stopwatch.GetTimestamp() - _lastUpdateTime < 100 * TickResolMs)
            {
                if (AbortEdiabasJob())
                {
                    break;
                }
                Thread.Sleep(100);
            }
            _lastUpdateTime = Stopwatch.GetTimestamp();
            DataUpdated?.Invoke(this, EventArgs.Empty);
        }

        private void ThreadTerminatedEvent()
        {
            ThreadTerminated?.Invoke(this, EventArgs.Empty);
        }
    }
}
