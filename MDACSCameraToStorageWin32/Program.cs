/*
 * This program simply switches compatible devices from configuration mode to mass storage mode.
 * 
 * The current list of known compatible devices are:
 *          BodyCam BC-300
*/
using System;
using System.Text;
using System.Threading;
using MonoLibUsb;
using MonoLibUsb.Profile;
using MonoLibUsb.Transfer;
using Usb = MonoLibUsb.MonoUsbApi;
using System.Runtime.InteropServices;
using System.IO;
using System.Runtime.Serialization;

namespace MDACSCameraToStorage
{
    public static class Switcher
    {
        static int wait;

        static void noop_usb_callback(MonoUsbTransfer transfer)
        {
            wait = 1;
        }

        static void config_op(MonoUsbDeviceHandle handle, byte endpoint, byte[] data, int readcnt)
        {
            int actual_length = 0;
            int res;

            byte[] recv_buf = new byte[1024];

            GCHandle data_gc = GCHandle.Alloc(data, GCHandleType.Pinned);
            GCHandle recv_buf_gc = GCHandle.Alloc(recv_buf, GCHandleType.Pinned);

            MonoUsbTransferDelegate d = noop_usb_callback;

            MonoUsbTransfer transfer = new MonoUsbTransfer(0);

            wait = 0;

            Console.WriteLine("data_gc addr = {0}", data_gc.AddrOfPinnedObject());
            Console.WriteLine("recv_buf_gc addr = {0}", recv_buf_gc.AddrOfPinnedObject());

            // Execute the write operation (asynchronous).
            transfer.FillBulk(handle, endpoint, data_gc.AddrOfPinnedObject(), data.Length, d, recv_buf_gc.AddrOfPinnedObject(), 4000);
            transfer.Submit();

            Thread.Sleep(300);

            // Execute the specified number of read operations (synchronous).
            for (int x = 0; x < readcnt; ++x)
            {
                res = Usb.BulkTransfer(handle, (byte)(endpoint | 0x80), recv_buf_gc.AddrOfPinnedObject(), recv_buf.Length, out actual_length, 4000);
                if (res != 0)
                {
                    throw new Exception("config_op Usb.BulkTransfer failure");
                }
                // Should only be here once the above transfer completes.
            }

            // Wait for the first write asynchronous to return.
            // Do not poll forever. Abort if it takes too long.
            var st = DateTime.Now;

            if (readcnt > 0)
            {
                while (wait == 0 && (DateTime.Now - st).TotalSeconds < 30)
                {
                    Thread.Sleep(100);
                }
            }

            data_gc.Free();
            recv_buf_gc.Free();
        }

        static bool HandleConfigModeDevice(MonoUsbProfile profile)
        {
            MonoUsbDeviceHandle handle;

            handle = profile.OpenDeviceHandle();

            if (handle == null || handle.IsInvalid)
            {
                return false;
            }

            Usb.ClaimInterface(handle, 0);

            var now = DateTime.Now.ToUniversalTime();

            var date_string = String.Format("{0:d4}/{1:d2}/{2:d2}/{3:d2}/{4:d2}/{5:d2}",
                now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second
            );

            var date_string_bytes = Encoding.ASCII.GetBytes(date_string);

            MemoryStream date_string_cmd = new MemoryStream();

            date_string_cmd.WriteByte(19);
            date_string_cmd.WriteByte(0);
            date_string_cmd.WriteByte(0);
            date_string_cmd.WriteByte(0);
            date_string_cmd.Write(date_string_bytes, 0, date_string_bytes.Length);

            byte[] seq0 = new byte[] { 69, 58, 2, 103, 203, 15, 16, 15, 9, 0, 0, 0, 186, 197, 253, 152 };
            byte[] seq1 = new byte[] { 69, 58, 2, 103, 6, 15, 16, 15, 10, 0, 0, 0, 186, 197, 253, 152 };
            byte[] seq2 = new byte[] { 69, 58, 2, 103, 179, 15, 16, 15, 132, 0, 0, 0, 186, 197, 253, 152 };
            byte[] seq3 = new byte[] { 69, 58, 2, 103, 17, 15, 16, 15, 12, 0, 0, 0, 186, 197, 253, 152 };
            byte[] seq4 = new byte[] { 69, 58, 2, 103, 16, 15, 16, 15, 5, 0, 0, 0, 186, 197, 253, 152 };
            byte[] seq5 = new byte[] { 69, 58, 2, 103, 212, 0, 0, 15, 23, 0, 0, 0, 186, 197, 253, 152 };

            //byte[] seq6 = new byte[] { 19,  0, 0,   0,  50,  48, 49,  55,  47,  48, 54, 47,  50,  55,  47,  50, 48, 47, 51, 56, 47, 50, 56 };

            byte[] seq6 = date_string_cmd.ToArray();

            byte[] seq7 = new byte[] { 69, 58, 2, 103, 204, 15, 16, 15, 5, 0, 0, 0, 186, 197, 253, 152 };
            byte[] seq8 = new byte[] { 69, 58, 2, 103, 160, 15, 16, 15, 10, 0, 0, 0, 186, 197, 253, 152 };

            // These two packets alone will switch the device into mass storage mode.
            byte[] seq9 = new byte[] { 69, 58, 2, 103, 247, 0, 0, 15, 10, 0, 0, 0, 186, 197, 253, 152 };
            byte[] seq10 = new byte[] { 6, 0, 0, 0, 88, 122, 84, 117, 86, 116 };

            config_op(handle, 1, seq0, 2);
            config_op(handle, 1, seq1, 2);
            config_op(handle, 1, seq2, 2);
            config_op(handle, 1, seq3, 2);
            config_op(handle, 1, seq4, 2);
            config_op(handle, 1, seq5, 0);
            config_op(handle, 1, seq6, 1);
            config_op(handle, 1, seq7, 2);
            config_op(handle, 1, seq8, 2);
            config_op(handle, 1, seq9, 0);
            config_op(handle, 1, seq10, 1);

            handle.Close();
            return true;
        }

        public static void SwitchAllFound()
        {
            MonoUsbSessionHandle session = new MonoUsbSessionHandle();

            if (session.IsInvalid)
            {
                throw new Exception("The USB session handle was invalid.");
            }

            MonoUsbProfileList list = new MonoUsbProfileList();

            if (list.Refresh(session) < 0)
            {
                throw new Exception("USB refresh profile list from session failed.");
            }

            // 0x4255:0x0001 (Config Mode)
            // 0x4255:0x1000 (Mass Storage Mode)

            foreach (MonoUsbProfile profile in list)
            {
                short vendor_id = profile.DeviceDescriptor.VendorID;
                short product_id = profile.DeviceDescriptor.ProductID;

                if (vendor_id == 0x4255 && product_id == 0x0001)
                {
                    // BodyCam Configuration Mode
                    HandleConfigModeDevice(profile);
                }

                profile.Close();
            }
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            Switcher.SwitchAllFound();
        }
    }
}
