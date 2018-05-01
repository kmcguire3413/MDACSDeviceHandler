This Windows targeting project scans for a BC-300 volume. Once found, it attempts to upload the data. It also unmounts the volume from its normal path in an attempt to restrict access to the device's storage medium.

This project uses Win32 API calls to unmount and mount volumes and to eject the BC-300 USB device.