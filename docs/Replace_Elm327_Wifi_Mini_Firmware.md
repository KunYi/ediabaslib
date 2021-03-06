# Replace ELM327 Wifi V1.5 HW: V01W_M_V1.0 adapter firmware

This chapter describes how to replace the ELM327 Wifi V1.5 HW: V01W_M_V1.0 adapter PIC18F25K80 and ESP8266ex firmware.  

### Requirements:

* [ELM327 Wifi V1.5 HW: V01W_M_V1.0 adapter](http://s.aliexpress.com/BBBv6fYJ)
* Usb to serial with 3.3v I/O
* PicKit 3/4 (to program the PIC18F25K80)

### ELM327 Wifi V1.5 HW: V01W_M_V1.0 board connections:

[![ELM327 Wifi V1.5 HW: V01W_M_V1.0 board progamming connections big](elm327_wifi_annotated_esp8266x_and_pic18f25k80_prog_connections_Small.png "ELM327 Wifi V1.5 HW: V01W_M_V1.0 board programming connections")](elm327_wifi_annotated_esp8266x_and_pic18f25k80_prog_connections_Big.png)

## Step1: Program the ESP8266ex Soc
* Connect your Usb to serial (take care ESP8266ex is not 5v tolerant!) to U0RXD (connects to TX), U0TXD (connects to RX) and GND
* Connect GPIO0 to GND (this forces the ESP8266ex into bootloader on next bootup)
* Connect MCLR to GND to force the PIC18F in high-Z
* Power the Elm327 adapter
* Flash ESP-link firmware to the ESP8266ex using the instructions for 8Mbit/1MByte flash from [ESP-link serial flashing](https://github.com/jeelabs/esp-link/blob/master/FLASHING.md#initial-serial-flashing)
* On Windows use [NodeMCU Flasher](https://github.com/nodemcu/nodemcu-flasher), this is easier to use.
* Disconnect GPIO0 from GND (all others stay), Power cycle the Elm327 adapter, connect to ESP_XXYYZZ wifi network.
* Using browser, browse to 192.168.4.1, select preset pin assignment: ESP-01, Change
* Goto Debug log page, select UART debug log: off; Goto uC Console, select Baud 38400 for `default` PIC firmware, 115200 for `esp8266` firmware.
* Optionally change network name in Wifi Soft-AP page

## Step2: Program the PIC18F25K80
* Connect your PicKit 3/4 to MCLR, PGD, PGC, GND (Vss) and 5V (Vcc) (take care, do not apply power from PicKit 3/4)
* Power the Elm327 adapter
* From subdirectory `CanAdapterElm` select either `default` firmware when using baudrate 38400 (take care slightly misallocated led usage) or `wifi_esp8266ex (correct name/fill)` (recommended) when using 115200 baudrate, always use `CanAdapterElm.X.production.unified.hex` for this first upload
* Flash the selected firmware to the PIC18F25K80

## Step3: Testing
* Power the Elm327 adapter
* Connect to ESP_XXYYZZ wifi network
* Telnet/Putty to 192.168.4.1 port 23 (use a program that allows hex display, send and recieve)
* When sending strings to the adapter you should at least get an echo from the adapter, otherwise there is a problem with the connections.  
You could test reading the ignition pin with the following command (hex values):  
`82 F1 F1 FE FE 60`  
The response is (additionally to the echo):  
`82 F1 F1 FE <state> <checksum>` with state bit 0 set to 1 if ignition is on.  
