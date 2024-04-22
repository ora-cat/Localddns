using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Localddns
{
    public enum ServiceState
    {
        SERVICE_STOPPED = 0x00000001,
        SERVICE_START_PENDING = 0x00000002,
        SERVICE_STOP_PENDING = 0x00000003,
        SERVICE_RUNNING = 0x00000004,
        SERVICE_CONTINUE_PENDING = 0x00000005,
        SERVICE_PAUSE_PENDING = 0x00000006,
        SERVICE_PAUSED = 0x00000007,
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatus
    {
        public int dwServiceType;
        public ServiceState dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    };

    public struct Record_Modify
    {
        public string login_token;
        public string format;
        public string domain;
        public string record_id;
        public string record_type;
        public string record_line;
        public string value;
        public string mx;
    }

    public struct Record_List
    {
        public string login_token;
        public string format;
        public string domain;
    }
    public partial class Localddns : ServiceBase
    {
        //=============================================================================================
        //                                   部分可用配置项
        //=============================================================================================

        //是否输出日志到文件(执行文件夹下的LocalddnsLog.txt)
        public bool bFileLog = false;

        //轮询时间间隔
        public double Interval = 60 * 1000 * 1;

        //缓存的IP值
        public string cachedIP = "0.0.0.0";

        //可以返回公网IP的请求接口
        public string public_ip_url = "http://ip.3dxr.com/ip";

        //UserAgent(详见 https://docs.dnspod.cn/api/5f55993d8ae73e11c5b01ce6/)
        public string user_agent = "LocalddnsClient/1.0.0";

        //DNSPod登陆密钥(详见 https://docs.dnspod.cn/account/5f2d466de8320f1a740d9ff3/)
        public string login_token = "302400,36b3933278ea93a4219d28dbe497f892";

        //=============================================================================================

        private readonly EventLog eventLog;
        private int eventID = 1;
        private string logFilePath = System.AppDomain.CurrentDomain.BaseDirectory + "LocalddnsLog.txt";

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetServiceStatus(System.IntPtr handle, ref ServiceStatus serviceStatus);

        public Localddns()
        {
            InitializeComponent();
            eventLog = new EventLog();
            if (!EventLog.SourceExists("LocalddnsSource"))
            {
                EventLog.CreateEventSource("LocalddnsSource", "LocalddnsLog");
            }

            eventLog.Source = "LocalddnsSource";
            eventLog.Log = "LocalddnsLog";
        }

        public void Log(string message,
            EventLogEntryType type = EventLogEntryType.Information,
            int eventID = 0,
            short category = 0,
            byte[] rawData = null)
        {
            //eventlog
            eventLog.WriteEntry(message, type, eventID,category,rawData);
            //filelog
            if (bFileLog)
            {
                using (FileStream stream = new FileStream(logFilePath, FileMode.Append))
                using (StreamWriter writer = new StreamWriter(stream))
                {
                    writer.WriteLine($"[{DateTime.Now}] {message}");
                }
            }
        }

        protected override void OnStart(string[] args)
        {
            // Update the service state to Start Pending.
            ServiceStatus serviceStatus = new ServiceStatus();
            serviceStatus.dwCurrentState = ServiceState.SERVICE_START_PENDING;
            serviceStatus.dwWaitHint = 100000;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);

            Log("In OnStart.");
            System.Timers.Timer timer = new System.Timers.Timer();
            timer.Interval = Interval;
            timer.Elapsed += new ElapsedEventHandler(this.OnTimer);
            timer.Start();

            // Update the service state to Running.
            serviceStatus.dwCurrentState = ServiceState.SERVICE_RUNNING;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);
        }

        private void OnTimer(object sender, ElapsedEventArgs e)
        {
            //throw new NotImplementedException();
            Log("Monitoring the System",EventLogEntryType.Information,eventID++);
            HttpWebRequest request = (HttpWebRequest) WebRequest.Create(public_ip_url);
            request.UseDefaultCredentials = true;
            request.UserAgent = user_agent;
            request.Method = "GET";
            request.ContentType = "text/html;charset=UTF-8";

            HttpWebResponse response = (HttpWebResponse) request.GetResponse();
            Stream responseStream = response.GetResponseStream();
            StreamReader streamReader = new StreamReader(responseStream, Encoding.UTF8);
            string responseString = streamReader.ReadToEnd();
            responseStream.Close();
            Log("Local public ip : " + responseString);
            if (cachedIP == responseString || string.IsNullOrWhiteSpace(responseString) || responseString.Length > 15)
            {
                Log("Local public ip has not changed or is invalid: " + responseString,EventLogEntryType.Warning);
                return;
            }
            cachedIP = responseString;
            ModifyDNS(responseString);
        }

        private string GetRecordList(string localIP)
        {
            string record_id = "";
            string url = "https://dnsapi.cn/Record.List";
            Record_List rl = new Record_List();
            rl.login_token = login_token;
            rl.format = "json";
            rl.domain = "mapping.host";
            string requestString = "";
            try
            {
                foreach (FieldInfo field in typeof(Record_List).GetFields())
                {
                    requestString += field.Name;
                    requestString += "=";
                    requestString += field.GetValue(rl).ToString();
                    requestString += "&";
                }
                if (!string.IsNullOrWhiteSpace(requestString))
                {
                    requestString = requestString.Substring(0, requestString.Length - 1);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Log("FieldInfo error : " + e,EventLogEntryType.Error);
                throw;
            }
            
            //string requestString = JsonConvert.SerializeObject(rl);
            byte[] requestBytes = Encoding.UTF8.GetBytes(requestString);

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.UseDefaultCredentials = true;
                request.UserAgent = user_agent;
                request.Method = "POST";
                request.ContentType = "application/x-www-form-urlencoded";
                request.ContentLength = requestBytes.Length;
                Stream requestStream = request.GetRequestStream();
                requestStream.Write(requestBytes, 0, requestBytes.Length);
                requestStream.Close();

                Log("Record.List request : " + requestString);

                HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                Stream responseStream = response.GetResponseStream();
                StreamReader streamReader = new StreamReader(responseStream, Encoding.UTF8);
                string responseString = streamReader.ReadToEnd();
                responseStream.Close();

                Log("Record.List response : " + responseString);

                JObject jObject = (JObject)JsonConvert.DeserializeObject(responseString);
                if (jObject["status"]["code"].ToString() == "1")
                {
                    foreach (var record in jObject["records"])
                    {
                        if(record["type"].ToString() == "A")
                        {
                            record_id = record["id"].ToString();
                            if(record["value"].ToString() == localIP)
                            {
                                Log("Old record value is the same : " + localIP, EventLogEntryType.Warning);
                                return null;
                            }
                            break;
                        }
                    }
                }
            }catch (Exception e)
            {
                Log("error : " + e);
            }
            
            return record_id;
        }

        private void ModifyDNS(string localIP)
        {
            string record_id = GetRecordList(localIP);
            if (string.IsNullOrWhiteSpace(record_id))
            {
                return;
            }
            Log("Record id is : " + record_id);

            string url = "https://dnsapi.cn/Record.Modify";
            Record_Modify rm = new Record_Modify();
            rm.login_token = login_token;
            rm.format = "json";
            rm.domain = "mapping.host";
            rm.record_id = record_id;
            rm.record_type = "A";
            rm.record_line = "默认";
            rm.value = localIP;
            rm.mx = "1";

            string requestString = "";
            try
            {
                foreach (FieldInfo field in typeof(Record_Modify).GetFields())
                {
                    requestString += field.Name;
                    requestString += "=";
                    requestString += field.GetValue(rm).ToString();
                    requestString += "&";
                }
                if (!string.IsNullOrWhiteSpace(requestString))
                {
                    requestString = requestString.Substring(0, requestString.Length - 1);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Log("FieldInfo error : " + e,EventLogEntryType.Error);
                throw;
            }
            
            //string requestString = JsonConvert.SerializeObject(rm);
            byte[] requestBytes = Encoding.UTF8.GetBytes(requestString);

            HttpWebRequest request = (HttpWebRequest) WebRequest.Create(url);
            request.UseDefaultCredentials = true;
            request.UserAgent = user_agent;
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded";
            request.ContentLength = requestBytes.Length;
            Stream requestStream = request.GetRequestStream();
            requestStream.Write(requestBytes, 0, requestBytes.Length);
            requestStream.Close();

            Log("Record.Modify request : " + requestString);

            HttpWebResponse response = (HttpWebResponse)request.GetResponse();
            Stream responseStream = response.GetResponseStream();
            StreamReader streamReader = new StreamReader(responseStream, Encoding.UTF8);
            string responseString = streamReader.ReadToEnd();
            responseStream.Close();

            Log("Record.Modify response : " + responseString);

            JObject jObject = (JObject) JsonConvert.DeserializeObject(responseString);
            if (jObject["status"]["code"].ToString() == "1")
            {
                Log("Modify record success.");
            }
            
        }

        protected override void OnPause()
        {
            Log("In OnPause.");
            base.OnPause();
        }

        protected override void OnContinue()
        {
            Log("In OnContinue.");
            base.OnContinue();
        }

        protected override void OnShutdown()
        {
            Log("In OnShutdown.");
            base.OnShutdown();
        }

        protected override void OnStop()
        {
            // Update the service state to Stop Pending.
            ServiceStatus serviceStatus = new ServiceStatus();
            serviceStatus.dwCurrentState = ServiceState.SERVICE_STOP_PENDING;
            serviceStatus.dwWaitHint = 100000;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);

            Log("In OnStop.");
            base.OnStop();

            // Update the service state to Stopped.
            serviceStatus.dwCurrentState = ServiceState.SERVICE_STOPPED;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);
        }
    }
}
