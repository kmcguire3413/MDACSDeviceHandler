using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Diagnostics;
using System.Threading.Tasks;
using MDACS;
using MDACS.API.Requests;
using System.Threading;
using MDACS.API.Responses;

namespace MDACSCameraStorageHandler
{
    static class Native
    {
        // Using IntPtr for LPCSTR because not sure if native should should be allowed to write into a string instance.
        [DllImport("kernel32")]
        public static extern UIntPtr FindFirstVolume(IntPtr lpszVolumeName, Int32 bufferSize);
        [DllImport("kernel32")]
        public static extern bool FindNextVolume(UIntPtr handle, IntPtr lpszVolumeName, Int32 bufferSize);
        [DllImport("kernel32")]
        public static extern bool FindVolumeClose(UIntPtr handle);
        [DllImport("kernel32")]
        public static extern UIntPtr FindFirstVolumeMountPoint(IntPtr lpszRootPathName, IntPtr lpszVolumeMountPoint, Int32 cchBufferLength);
        [DllImport("kernel32")]
        public static extern bool FindNextVolumeMountPoint(UIntPtr handle, IntPtr lpszVolumeMountPoint, Int32 cchBufferLength);
        [DllImport("kernel32")]
        public static extern bool FindVolumeMountPointClose(UIntPtr handle);
        [DllImport("kernel32")]
        public static extern bool GetVolumePathNamesForVolumeName(string volumeName, byte[] volumePathNames, Int32 cchBufferLength, out Int32 count);
        [DllImport("kernel32")]
        public static extern bool DeleteVolumeMountPoint(string volumeMountPoint);
        [DllImport("kernel32")]
        public static extern bool GetVolumeInformation(string rootPathName, StringBuilder volumeName, Int32 volumeNameSize, out Int32 serialNumber, out Int32 maxComponentLength, out Int32 fsFlags, StringBuilder fsNameBuffer, Int32 fsNameBufferSize);
        [DllImport("kernel32")]
        public static extern bool SetVolumeMountPoint(string mountPoint, string volName);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr CreateFile(
         string lpFileName,
         uint dwDesiredAccess,
         uint dwShareMode,
         IntPtr SecurityAttributes,
         uint dwCreationDisposition,
         uint dwFlagsAndAttributes,
         IntPtr hTemplateFile
        );    
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped
        );
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            byte[] lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped
        );
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(
            IntPtr hObject
        );
        [DllImport("kernel32.dll")]
        public static extern bool QueryDosDevice(string deviceName, StringBuilder targetPath, Int32 ucchMax);
    }

    struct VolumeInfo
    {
        public string GUIDPath;
    }

    class VolumeEnumerator
    {
        UIntPtr handle;
        byte[] buff;
        GCHandle buff_gch;
        IntPtr buff_addr;
        bool closed;

        VolumeInfo info;

        public VolumeEnumerator()
        {
            closed = false;
            buff = new byte[1024 * 4];
            buff_gch = GCHandle.Alloc(buff, GCHandleType.Pinned);
            buff_addr = buff_gch.AddrOfPinnedObject();
            // Hoping that TCHAR is never wider than four bytes for UTF-32. That is the
            // worst possible case I can envision. This should be replaced with something
            // that determines the string encoding used by kernel32.
            handle = Native.FindFirstVolume(buff_addr, buff.Length / 4);
            ProcessVolume();
        }

        public bool Next()
        {
            if (closed)
            {
                return false;
            }

            Array.Clear(buff, 0, buff.Length);
            Native.FindNextVolume(handle, buff_addr, buff.Length / 4);

            if (!ProcessVolume())
            {
                Close();
                return false;
            }


            return true;
        }

        public VolumeInfo GetVolumeInfo()
        {
            return info;
        }

        ~VolumeEnumerator()
        {
            Close();
        }

        static string KernelStringBytesToString(byte[] buff)
        {
            // Problem is determining the length of a string with an unknown encoding. So,
            // best case is ASCII and worst case is others but at least we can fail gracefully.
            int x;

            for (x = 0; (x < buff.Length) && (buff[x] != 0); ++x) ;

            return Encoding.ASCII.GetString(buff, 0, x);
        }

        bool ProcessVolume()
        {
            var volumeGUIDPath = KernelStringBytesToString(buff);

            if (volumeGUIDPath.Length == 0)
            {
                return false;
            }

            info = new VolumeInfo()
            {
                GUIDPath = volumeGUIDPath,
            };

            return true;
        }

        public void Close()
        {
            if (closed)
            {
                return;
            }

            closed = true;

            buff_gch.Free();

            Native.FindVolumeClose(handle);
        }

        public static List<VolumeInfo> GetVolumesAsList()
        {
            var ve = new VolumeEnumerator();
            var ret = new List<VolumeInfo>();

            do
            {
                ret.Add(ve.info);
            } while (ve.Next());

            return ret;
        }
    }

    struct ListTTLItem
    {
        public string text;
        public long time;
    }

    class ListTTLPersistentData
    {
        public List<ListTTLItem> items;
    }

    class ListTTL
    {
        ListTTLPersistentData data;
        string path;

        public ListTTL(float seconds, string path)
        {
            if (File.Exists(path))
            {
                data = JsonConvert.DeserializeObject<ListTTLPersistentData>(File.ReadAllText(path));

                if (data == null || data.items == null)
                {
                    data.items = new List<ListTTLItem>();
                }
            } else
            {
                data = new ListTTLPersistentData();
                data.items = new List<ListTTLItem>();
            }

            this.path = path;

            FlushOld(seconds);
        }

        ~ListTTL()
        {
            Close();
        }

        void FlushOld(float seconds)
        {
            var now = DateTime.Now.ToFileTimeUtc();

            for (int x = 0; x < data.items.Count; ++x)
            {
                var delta = (float)(now - data.items[x].time) * 0.0000001;

                if (delta > seconds)
                {
                    data.items.RemoveAt(x);
                    --x;
                }
            }
        }

        public void Add(string text)
        {
            data.items.Add(new ListTTLItem
            {
                text = text,
                time = DateTime.Now.ToFileTimeUtc(),
            });
        }

        public bool Contains(string text)
        {
            foreach (var ctext in data.items)
            {
                if (ctext.text.Equals(text))
                {
                    return true;
                }
            }

            return false;
        }

        public void Close()
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(data));
        }
    }

    public struct ServerDeviceConfig
    {
        public string userId;
        public string deviceConfig;
    }

    public struct ServerCredentials
    {
        public string serverUrl;
    }


    class ServerRequestConfigMessage
    {
        public string deviceid;
        public string current_config_data;
    }

    class ServerResponseConfigMessage
    {
        public string userid;
        public string config_data;
    }

    public class ServerUtility
    {
        public static ServerDeviceConfig GetConfiguration(ServerCredentials credentials, string deviceId, string currentConfigData)
        {
            var req = WebRequest.Create($"{credentials.serverUrl}/device-config");

            var reqMessage = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new DeviceConfigRequest()
            {
                deviceid = deviceId,
                current_config_data = currentConfigData,
            }));

            req.Method = "POST";
            req.ContentType = "text/json";
            req.ContentLength = reqMessage.Length;

            var reqTx = req.GetRequestStream();
            reqTx.Write(reqMessage, 0, reqMessage.Length);
            reqTx.Close();

            var reqRx = req.GetResponse().GetResponseStream();
            var reqRxReader = new StreamReader(reqRx);

            var rxData = reqRxReader.ReadToEnd();

            var res = JsonConvert.DeserializeObject<ServerResponseConfigMessage>(rxData);
            var res2 = JsonConvert.DeserializeObject<ServerResponseConfigMessage>(res.config_data);

            return new ServerDeviceConfig {
                userId = res2.userid,
                deviceConfig = res2.config_data,
            };
        }
    }

    class ServerUploadHeader
    {
        public string datestr;
        public string timestr;
        public string devicestr;
        public string userstr;
        public string datatype;
        public ulong datasize;
    };

    class ProgramConfig
    {
        public string authUrl;
        public string dbUrl;
        public string username;
        public string password;
    }

    class Program
    {
        static bool IsBC300ConfigTheSame(string a, string b)
        {
            return JsonConvert.DeserializeObject(a).Equals(JsonConvert.DeserializeObject(b));
        }

        static bool UploadDataToServer(ServerCredentials credentials, ServerUploadHeader header, string dataFilePath)
        {
            header.datasize = (ulong)(new FileInfo(dataFilePath)).Length;

            var headerJson = JsonConvert.SerializeObject(header);
            var headerBytes = Encoding.UTF8.GetBytes(headerJson);

            return true;
        }

        static bool HandleVolumeMountForUpload(string path, ProgramConfig config)
        {
            Console.WriteLine($"HandleVolumeMountForUpload: handling volume at path {path} for upload");

            if (!IsVolumeMountPointForUploadValid(path))
            {
                Console.WriteLine($"HandleVolumeMountForUpload: mount point for volume failed inner validity check");
                return false;
            }

            var configText = File.ReadAllText($"{path}MISC\\settings.json");
            var configParsed = JsonConvert.DeserializeObject<JObject>(configText);
            var serialNumber = configParsed["info"]["serial-number"].ToObject<string>();

            var creds = new ServerCredentials()
            {
                serverUrl = config.dbUrl,
            };

            var serverConfig = ServerUtility.GetConfiguration(creds, serialNumber, configText);

            if (IsBC300ConfigTheSame(configText, serverConfig.deviceConfig))
            {
                File.WriteAllText($"{path}MISC\\settings.json", serverConfig.deviceConfig);
            }

            var serverReportedUserId = serverConfig.userId;

            // Scan for data files.
            foreach (var sdir in Directory.EnumerateDirectories($"{path}DCIM\\"))
            {
                foreach (var sfile in Directory.EnumerateFiles(sdir))
                {
                    var baseName = Path.GetFileNameWithoutExtension(sfile);
                    var baseParts = baseName.Split('_');

                    if (baseParts.Length < 3)
                    {
                        continue;
                    }

                    var fullPath = sfile;
                    var dataSize = new FileInfo(fullPath).Length;
                    var dataType = Path.GetExtension(sfile).Substring(1).ToLower();
                    var dateString = baseParts[2].Substring(0, 8);
                    var dateDay = dateString.Substring(2, 2);
                    var dateMonth = dateString.Substring(0, 2);
                    var dateYear = dateString.Substring(4, 4);

                    dateString = $"{dateYear}-{dateMonth}-{dateDay}";

                    var timeString = baseParts[2].Substring(8, 6);

                    var dataStream = File.OpenRead(fullPath);

                    Console.WriteLine($"Uploading {fullPath}");

                    var uploadTask = MDACS.API.Database.UploadAsync(
                        config.authUrl, config.dbUrl, 
                        config.username, config.password,
                        dataSize, dataType, 
                        dateString, serialNumber, timeString,
                        serverReportedUserId, 
                        dataStream
                    );

                    Console.WriteLine("Waiting...");

                    uploadTask.Wait();

                    Console.WriteLine("Done upload..");

                    var uploadResult = uploadTask.Result;

                    dataStream.Close();

                    if (uploadResult.success && uploadResult.security_id.Length != 0)
                    {
                        Console.WriteLine("Upload success");
                        File.Delete(fullPath);
                    } else
                    {
                        Console.WriteLine("Upload failure");
                    }
                }
            }

            // Would be nice to be able to run a chkdsk /F command on the volume just to help over time correct some issues.

            /*var processInfo = new ProcessStartInfo()
            {
                FileName = "chkdsk.exe",
                Arguments = $"{path} /f /r /x",
            };

            var chkdskProcess = Process.Start(processInfo);
            chkdskProcess.StandardInput.WriteLine("Y");
            chkdskProcess.StandardInput.WriteLine("Y");
            chkdskProcess.StandardInput.WriteLine("Y");
            chkdskProcess.WaitForExit(1000 * 60 * 5);
            */

            return true;
        }

        static void MountVolumeSpecialAndUpload(string volumeName, string guidPath, ProgramConfig config)
        {
            if (Directory.CreateDirectory($".\\{volumeName}") != null)
            {
                Native.SetVolumeMountPoint($".\\{volumeName}\\", guidPath);

                if (IsVolumeMountPointForUploadValid($".\\{volumeName}\\"))
                {
                    try
                    {
                        HandleVolumeMountForUpload($".\\{volumeName}\\", config);
                    } catch (Exception ex)
                    {
                        // Get out and turn it off so we do not overheat the device
                        // from continual processing through an infinite cycle of reading
                        // and erroring.
                        Console.WriteLine("HandleVolumeMountForUpload Exception:");
                        Console.WriteLine(ex.ToString());
                        Console.WriteLine(ex.StackTrace);
                        Console.WriteLine("Ejecting the device.");
                    }

                    // https://msdn.microsoft.com/en-us/library/aa363216(VS.85).aspx
                    const uint GENERIC_READ = 0x80000000;
                    const uint GENERIC_WRITE = 0x40000000;
                    const int FILE_SHARE_READ = 0x1;
                    const int FILE_SHARE_WRITE = 0x2;
                    const int FSCTL_LOCK_VOLUME = 0x00090018;
                    const int FSCTL_DISMOUNT_VOLUME = 0x00090020;
                    const int IOCTL_STORAGE_EJECT_MEDIA = 0x2D4808;
                    const int IOCTL_STORAGE_MEDIA_REMOVAL = 0x002D4804;

                    https://msdn.microsoft.com/en-us/library/windows/desktop/aa365461(v=vs.85).aspx
                    var devicePath = new StringBuilder(256);

                    var guidOnly = guidPath.Substring(4);

                    guidOnly = guidOnly.Substring(0, guidOnly.Length - 1);

                    // Note: Microsoft's historic naming reasons lead to decreased ability to recognize the needed API functions
                    //       unless I have been doing WIN32 development for the past 40 years.
                    Native.QueryDosDevice(guidOnly, devicePath, 256);

                    Native.DeleteVolumeMountPoint($".\\{volumeName}\\");

                    var neededDeviceString = devicePath.ToString();

                    neededDeviceString = neededDeviceString.Substring(neededDeviceString.LastIndexOf('\\') + 1);

                    var f = Native.CreateFile(
                        $"\\\\.\\{neededDeviceString}",
                        GENERIC_READ | GENERIC_WRITE,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, 0x3, 0x40000000, IntPtr.Zero
                    );

                    uint bytesReturned;

                    int tryCount = 0;

                    while (!Native.DeviceIoControl(f, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out bytesReturned, IntPtr.Zero))
                    {
                        Console.WriteLine("Trying to lock the volume.");
                        Thread.Sleep(1000);
                        tryCount++;

                        if (tryCount > 30)
                        {
                            break;
                        }
                    }

                    Native.DeviceIoControl(f, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out bytesReturned, IntPtr.Zero);

                    byte[] buf = new byte[1];
                    uint retVal;

                    buf[0] = 1;

                    Native.DeviceIoControl(f, IOCTL_STORAGE_MEDIA_REMOVAL, buf, 1, IntPtr.Zero, 0, out retVal, IntPtr.Zero);
                    Native.DeviceIoControl(f, IOCTL_STORAGE_EJECT_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out bytesReturned, IntPtr.Zero);
                }
                else
                {
                    Native.DeleteVolumeMountPoint($".\\{volumeName}\\");
                }
            }
        } 

        static bool IsVolumeMountPointForUploadValid(string path)
        {
            var a = File.Exists($"{path}MISC\\settings.json");

            Console.WriteLine($"IsVolumeMountPointForUploadValid: {path}");

            if (a == false)
            {
                Console.WriteLine("IsVolumeMountPointForUploadValid: failed to find settings.json");
                return false;
            }

            var b = Directory.Exists($"{path}\\DCIM");

            if (b == false)
            {
                Console.WriteLine("IsVolumeMountPointForUploadValid: failed to find DCIM folder");
            }

            Console.WriteLine($"IsVolumeMountPointForUploadValid: good");

            return true;
        }

        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Error: The JSON format program config file path must be specified as the first argument.");
                return;
            }

            if (!File.Exists(args[0]))
            {
                Console.WriteLine("Error: The JSON format program config file path does not exist.");
                return;
            }

            var config = JsonConvert.DeserializeObject<ProgramConfig>(File.ReadAllText(args[0]));

            var idTimeout = new ListTTL(60, "idTimeout.json");

            List<VolumeInfo> volumes = VolumeEnumerator.GetVolumesAsList();

            var mountPointsData = new byte[4096];

            foreach (var vol in volumes)
            {
                Console.WriteLine($"{vol.GUIDPath}");

                Int32 charsWritten;

                Array.Clear(mountPointsData, 0, mountPointsData.Length);

                Native.GetVolumePathNamesForVolumeName(vol.GUIDPath, mountPointsData, mountPointsData.Length, out charsWritten);

                var mountPoints = new List<string>();

                int y = 0;

                for (int x = 0; x < mountPointsData.Length; ++x)
                {
                    if (mountPointsData[x] == 0)
                    {
                        var delta = x - y;

                        if (mountPointsData[x] == 0 && mountPointsData[y] == 0)
                        {
                            break;
                        }

                        mountPoints.Add(Encoding.UTF8.GetString(mountPointsData, y, delta));

                        y = x;
                    }
                }

                var volumeNameSb = new StringBuilder(256);
                var fsName = new StringBuilder(256);

                Int32 serialNum;
                Int32 componentLength;
                Int32 fsFlags;

                Native.GetVolumeInformation(vol.GUIDPath, volumeNameSb, volumeNameSb.Capacity, out serialNum, out componentLength, out fsFlags, fsName, fsName.Capacity);

                var volumeName = volumeNameSb.ToString();

                if (volumeName.Length == 0)
                {
                    volumeName = "NONAME";
                }

                if (idTimeout.Contains(volumeName))
                {
                    Console.WriteLine($"Main: volume rejected on basis of idTimeout {vol.GUIDPath}");
                    continue;
                }

                idTimeout.Add(volumeName);

                idTimeout.Close();

                if (mountPoints.Count > 0)
                {
                    string valid = null;

                    Console.WriteLine($"Main: volume {vol.GUIDPath} HAS mounts; evaluating if any are valid");

                    foreach (var mountPoint in mountPoints)
                    {
                        Console.WriteLine($"Main: checking mount-point {mountPoint}");
                        if (IsVolumeMountPointForUploadValid(mountPoint))
                        {
                            Console.WriteLine($"Main: mount-point was valid");
                            valid = mountPoint;
                            break;
                        }
                    }

                    if (valid == null)
                    {
                        Console.WriteLine($"Main: no mount points were valid; ignoring volume");
                        continue;
                    }

                    Console.WriteLine($"Main: removing mount points to protect data from user access");
                    foreach (var mountPoint in mountPoints)
                    {
                        Native.DeleteVolumeMountPoint(mountPoint);
                    }

                    Console.WriteLine($"Main: mounting as {volumeName}");
                    MountVolumeSpecialAndUpload(volumeName, vol.GUIDPath, config);
                } else
                {
                    Console.WriteLine($"Main: volume {vol.GUIDPath} has no mounts; mounting as {volumeName}");
                    MountVolumeSpecialAndUpload(volumeName, vol.GUIDPath, config);
                }
            }

            Console.WriteLine("Done");
        }
    }
}
