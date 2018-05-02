This project provides the Microsoft Windows version of the device handler. It currently supports the BC-300 camera device.

Two sub-projects provide the ability to switch the BC-300 from configuration mode into mass storage mode and then access the mass storage mode in a secure manner to upload the data into the database.

Note: The way in which the camera handles this is rather insecure and a better method for devices would be to include a strong public certificate which signs the data. Then, access by unauthorized users can be proven to not have resulted in modification of data and a block type chain could provide the ability to determine if data is missin from the chain.
