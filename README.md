# QDTool

### Simple tool for converting and editing different types of SHARP MZ QuickDisk and tape files between each other - QDF, MZQ, MZT, MZF, ...
You can also reorder, add or delete files. Drag&Drop is supported. For example, in addition to QuickDisk files, you can easily create a multi-file MZT file for UniCMT with files in the correct order for sequential load. QDTool also includes a simple file browser.

<img width="786" height="443" src="https://github.com/mlukasek/QDTool/blob/c911edfe775f9a6410da1737b589abbe53808668/images/QDTool_scr_2024-02-25.png">

### Currently QDTool supports following file types:
**QDF** - Japan QuickDisk file format created for emulators, supported by EmuZ-1500, QDC, VirtuaQD tools.  
**MZQ** - European QuickDisk file format with simpler structure without gaps, suppoted by Unicard for SHARP MZ-700/800/1500 and SHARP MZ emulators from Zdenek Adler, Michal Hucik, Bohumil Novacek, etc.  
**QD** - Content-detected Sharp/MZ legacy logical images and physical HxC (`HXCQDDRV`) or FlashFloppy Quickdisk images. Save As offers each Quickdisk image container variant explicitly.

In Advanced mode, **New...** creates an empty MZQ or selected `.qd` variant, and **Save As...** can also create empty MZT, MZQ, QDF, or `.qd` images. Quickdisk details and **Format...** are shown only for the current `.qd` image; formatting empties it without changing its container or physical geometry. Advanced Open/Add also imports WAV, LEP, and L16 tape waveforms and maps supported NORMAL, MZ700, IC, and TC timing profiles back to the Loader and Speed columns. Standard MZ-800 editing is limited to 34 directory entries; Advanced mode identifies imported 35–50-entry images without discarding their data.
**MZF** - Tape file conatining header and data, as on tape, supported by most SHARP MZ emulators, UniCMT and others, the extension for the same file type is sometimes also M12 or MZT.  
**MZT** - Multiple MZF tape files concatenated one after the other as on tape, supported by MZ700Win, UniCMT.  
**LEP** - Export to a compact signed pulse-duration stream with 50 microsecond resolution.
**L16** - Export to a signed pulse-duration stream with finer 16 microsecond resolution.
**WAV** - Export to standard 44.1 kHz, 8-bit mono PCM audio for playback into a SHARP MZ-700/800. Fractional edge durations are preserved over time by accumulated quantization-error correction.

LEP, L16 and WAV exports use the loader and speed assigned to each record. NORMAL 1:1 through 1:4, MZ700 1:1/FAST3, IC 1:2 through 1:4 and TC 1:2 through 1:3 are generated from the MZ-SD2CMT2-Reborn timings and loader routines. UL profiles require a live WRITE/SENSE handshake and are therefore rejected for static waveform export. The compact stream contains only the blocks required by the selected profile; optional monitor backup copies are not written.

All waveform profiles use 11000 SHORT pulses for a header leader and 5500 SHORT pulses for every data/loader leader. NORMAL 1:1 uses context-dependent LOW widths from the MZ-800 1Z-013B ROM; NORMAL/IC accelerated timings use the Intercopy V10.2 writer rows (the historical 1:3 and 1:4 labels mean 7:3 and 8:3), and TC 1:2/1:3 uses the Turbo Copy V1.22 8253 counter model.

### There is work in progress on support for the following file types:
**RAW** - Raw data grabbed from QuickDisk by QDC.  
**MFM** - MFM data converted from RAW by QDC.  

### Requirements
QDTool is a Windows WPF application and requires the .NET 10 Desktop Runtime to run. It is written in C# in Microsoft Visual Studio 2022.

### Basic and Advanced modes
QDTool starts in Basic mode. Basic keeps the simple QDF/MZQ/QD/MZT/MZF workflow and hides waveform formats, tape profiles, sidecar metadata and trailing-data controls. Basic MZF saves omit trailing bytes; MZT saves always omit per-record trailing bytes.

Enable **Enable advanced features** to show LEP/L16/WAV export and per-record **Loader** and **Speed** dropdown columns. Select multiple rows with Ctrl/Shift and changing either dropdown applies that complete, valid loader/speed profile to every selected record. The MZ-700/MZ-800 choice in the waveform save dialog is only the fallback for records without an explicit profile; an assigned row profile always wins. For multiple records, the same dialog offers **Union** (one output containing every record) or **Separate** (numbered output files, one per record). Switching modes changes only the interface and never changes loaded data.

Matching SD2CMT2 `.MFI` and `.MTI` sidecars are loaded automatically. A save never creates a new sidecar in Basic mode, but an already existing target sidecar is regenerated so it cannot remain inconsistent with the saved MZF/MZT. In Advanced mode, the MZF/MZT save-options dialog offers **Generate MFI** or **Generate MTI**; MZF trailing-data preservation is selected in the same context-specific dialog.

### Releases
**2024-02-25  0.1.0 alpha** - first alpha release

### Known bugs
**0.1.0 alpha**
- No known display issues in the current source version.

##### QDTool<br/>Copyright (C) 2024 Martin Lukasek <martin@8bity.cz>, www.8bity.cz  
###### This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
###### This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
###### You should have received a copy of the GNU General Public License along with this program. If not, see <https://www.gnu.org/licenses/>.
