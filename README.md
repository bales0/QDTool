# QDTool

QDTool is a utility for converting, inspecting and editing SHARP MZ QuickDisk and tape files.

> **This project is based on the original [QDTool by Martin Lukasek](https://github.com/mlukasek/QDTool).**
>
> The original QDTool workflow is intentionally preserved as the default **Basic mode**.  
> Additional tape, waveform, loader, timing and advanced QuickDisk functions are hidden by default and become available only after enabling **Enable advanced features**.

The goal of this fork is to keep the original application simple for normal MZF/MZT/MZQ/QDF/QuickDisk work, while also providing a more complete toolset for SHARP MZ-700/MZ-800 tape and QuickDisk preservation, conversion and analysis.

<img width="786" height="443" src="/images/QDTool_scr_2026-09-19.png">

## Main functions

### Basic mode

QDTool starts in **Basic mode**. This mode follows the original QDTool concept and exposes only the commonly used file-editing functions.

Basic mode provides:

- opening and saving SHARP MZ QuickDisk and tape files,
- adding files to an existing image or tape,
- deleting files,
- reordering files with the arrow buttons or by dragging one or more selected rows,
- adding files by dragging them from Explorer,
- editing MZF header information,
- a simple file/hex browser,
- creation and editing of MZT multi-file tapes,
- conversion between the supported logical QuickDisk and tape formats,
- standard MZ-800 QuickDisk directory handling.

Advanced tape profiles, waveform files, sidecar metadata and low-level QuickDisk options remain hidden until **Enable advanced features** is switched on.

Switching between Basic and Advanced mode changes only the available user interface. It does not modify the data already loaded into the application.

## Advanced mode

Enable **Enable advanced features** to unlock the extended functions added in this fork.

### Tape profiles and loader selection

Each tape record can be assigned a **Loader** and **Speed** profile.

Currently supported profiles include:

- NORMAL 1:1
- NORMAL 1:2
- NORMAL 1:3
- NORMAL 1:4
- MZ700 1:1
- MZ700 FAST3
- IC 1:1
- IC 1:2
- IC 1:3
- IC 1:4
- TC 1:1
- TC 1:2
- TC 1:3
- UL / UL_MZ800 / UL_MZ700 metadata profiles

Multiple rows can be selected with Ctrl/Shift. Changing the Loader or Speed for a multi-selection applies the complete compatible profile to all selected records.

UL profiles use a live WRITE/SENSE handshake and therefore cannot be exported as a static WAV/LEP/L16 waveform.

### Tape waveform import

Advanced mode can import:

- WAV
- FLAC
- LEP
- L16

WAV and FLAC can be opened using either the normal decoder or the **heuristic audio analyzer**.

The heuristic analyzer is intended mainly for real analogue cassette recordings. It can examine:

- mono and stereo recordings,
- individual left/right channels,
- normal and inverted signal interpretation,
- zero-crossing and Schmitt-trigger pulse extraction,
- duplicate tape copies,
- checksum-valid headers and payloads,
- NORMAL, MZ700, Intercopy and Turbo Copy structures,
- tape timing and pulse statistics,
- selective recovery when a normal decode does not produce a valid payload.

The final result is converted directly into QDTool tape records; no intermediate WAV, MZF or MZT file is required.

The statistics window shows the selected source, checksum state, loader/profile evidence and measured timing information for recovered records.

### Tape waveform export

Advanced mode can export tape records to:

- WAV
- LEP
- L16

WAV export uses standard 44.1 kHz, 8-bit mono PCM suitable for playback into SHARP MZ cassette input hardware.

LEP and L16 store signed pulse durations:

- **LEP** - 50 microsecond units
- **L16** - 16 microsecond units

LEP/L16 pulse widths are stored independently. WAV keeps a sample-phase error accumulator so fractional pulse timing is preserved over the complete waveform.

Waveform export uses the Loader and Speed profile assigned to every record.

For multiple selected records, waveform export can create:

- **Union** - one waveform containing all selected records in order,
- **Separate** - numbered output files, one waveform per record.

### MZF and MZT export

Advanced export supports both selected records and the complete document.

- One record can be exported as a single MZF.
- Multiple records can be exported as one MZT.
- Multiple records can also be exported as separate numbered MZF files.
- Record order follows the order shown in the main grid.

### MFI and MTI sidecars

QDTool supports SD2CMT-style metadata sidecars:

- `.MFI` for a single MZF,
- `.MTI` for an MZT containing multiple records.

Matching sidecars are loaded automatically.

In Basic mode, QDTool does not create a new sidecar, but an already existing sidecar is regenerated when necessary so it cannot remain inconsistent with the saved tape file.

In Advanced mode, the save dialog can explicitly create MFI/MTI metadata.

These metadata files are intended for use with **MZ-SD2CMT2-Reborn**. QDTool can create:

- `.MFI` metadata for `.MZF` files,
- `.MTI` metadata for `.MZT` multi-file tapes,
- `.LEP` pulse files,
- `.L16` pulse files.

This makes it possible to prepare tape files and their Loader/Speed metadata directly in QDTool for playback with MZ-SD2CMT2-Reborn.

MZ-SD2CMT2-Reborn repository:

https://github.com/bales0/MZ-SD2CMT2-Reborn

The format used by this project is documented in:

`specification/MFI_MTI_FORMAT.md`

### Trailing MZF data

Advanced mode allows preservation or removal of trailing data located after the normal MZF body.

Basic mode keeps the simpler original workflow and omits trailing bytes when creating a new MZF/MZT output.

### Advanced QuickDisk functions

Advanced mode adds lower-level QuickDisk handling while preserving the original logical editing workflow.

Supported `.qd` image variants are detected from file content and include:

- SHARP/MZ legacy logical QuickDisk images,
- HxC QuickDisk images (`HXCQDDRV`),
- FlashFloppy QuickDisk images.

Advanced mode also provides:

- explicit selection of the `.qd` container type when saving,
- creation of an empty MZQ or selected `.qd` image,
- QuickDisk image details,
- formatting of the current `.qd` image without changing its physical container/geometry,
- detection and preservation of imported images containing more than the standard 34 MZ-800 directory entries.

Standard editing uses the normal SHARP MZ-800 limit of 34 directory entries. Advanced mode can identify and retain compatible imported images containing approximately 35-50 entries without silently discarding their existing contents.

## Supported file formats

### QDF

Japanese QuickDisk logical file format used by several SHARP tools and emulators.

### MZQ

European QuickDisk logical format with a simpler structure. It is used by UniCard and several SHARP MZ emulators and utilities.

### QD

QuickDisk image container. QDTool detects supported SHARP/MZ legacy, HxC and FlashFloppy QuickDisk variants from their contents.

### MZF

Single SHARP MZ tape file containing the 128-byte file header followed by the file body.

The same or closely related tape data is also found with extensions such as M12.

### MZT

Multiple MZF records concatenated in tape order.

MZT is useful for preserving multi-part programs and for creating correctly ordered sequential tapes for UniCMT and emulators.

### LEP

Compact signed pulse-duration tape stream using 50 microsecond units.

### L16

Signed pulse-duration tape stream using 16 microsecond units.

### WAV

PCM tape waveform.

QDTool can generate 44.1 kHz 8-bit mono WAV files and Advanced mode can analyze supported PCM WAV recordings at several common sample rates and bit depths.

### FLAC

FLAC is supported as an Advanced-mode **input** format for audio/tape analysis.

FLAC output is not generated by QDTool.

## Tape timing

Waveform generation and loader recognition are based on SHARP MZ ROM/Z80 timing analysis and on the original accelerated loader implementations.

The generated conventional tape structure uses:

- 11000 SHORT pulses for a header leader,
- 5500 SHORT pulses for a data/loader leader.

NORMAL 1:1 uses timing derived from the MZ-800 1Z-013B ROM, including context-dependent LOW widths.

MZ700 timing is based on the corresponding MZ-700 ROM tape routines.

Accelerated NORMAL/IC timing follows the Intercopy V10.2 writer timing.

Turbo Copy timing follows the Turbo Copy V1.22 loader/writer implementation and its timer model.

The MZ700/NORMAL, IC and TC metadata detected during audio analysis is kept separate from the physical waveform decoder so that structural loader evidence can be used where available.

## QuickDisk limits

A standard SHARP MZ-800 QuickDisk directory contains up to 34 files.

Advanced mode can recognize some non-standard/extended images containing more entries and preserves their existing contents whenever possible.

The actual usable capacity also depends on the selected QuickDisk image format and physical layout.

## Requirements

QDTool is currently a Windows WPF application.

- .NET 10 Desktop Runtime
- Windows
- C# / Visual Studio 2022 or a compatible .NET development environment

FLAC input uses `NAudio.SoundFile` together with the bundled `libsndfile` Windows runtime.

## Work in progress

Possible future/experimental formats include:

- **RAW** - raw QuickDisk data captured by QDC or similar hardware/software,
- **MFM** - decoded/converted MFM-level QuickDisk data.

These formats should not be considered stable until explicitly listed as supported.

## Project history

### Original QDTool

The original QDTool was created by **Martin Lukasek** as a simple application for converting and editing SHARP MZ QuickDisk and tape files.

Original repository:

https://github.com/mlukasek/QDTool

This fork keeps that application and its Basic-mode workflow as its foundation.

### Extended fork

This repository extends the original project mainly with:

- Advanced/Basic user-interface separation,
- additional QuickDisk image variants,
- physical QuickDisk image handling,
- tape Loader/Speed metadata,
- NORMAL/MZ700/Intercopy/Turbo Copy waveform generation,
- IC 1:1 and TC 1:1 support,
- LEP/L16/WAV waveform import and export,
- FLAC audio input,
- heuristic analogue tape analysis,
- MFI/MTI sidecars,
- multi-selection and generalized Export/Export All functions,
- additional validation and preservation functions.

## License

QDTool is distributed under the GNU General Public License version 3.

Original QDTool copyright:

Copyright (C) 2024 Martin Lukasek  
https://www.8bity.cz/

This project is provided without warranty; see `LICENSE.txt` for the full GPLv3 license text.

## Technical references and source projects

The extended functions in this fork were developed and verified using information, code concepts, file-format behavior and timing data from the following projects and documentation:

- **Original QDTool by Martin Lukasek** - the base project from which this fork was created  
  https://github.com/mlukasek/QDTool

- **MZ-SD2CMT by SHARPENTIERS** - original SD-card CMT implementation for the SHARP MZ family and an important reference for tape loaders, formats and metadata  
  https://github.com/SHARPENTIERS/MZ-SD2CMT

- **MZ-SD2CMT2-Reborn** - development/reference fork used while extending loader profiles, tape timing and MFI/MTI behavior. QDTool can generate `.MFI` metadata for `.MZF`, `.MTI` metadata for `.MZT`, and `.LEP`/`.L16` pulse files for use with this project.  
  https://github.com/bales0/MZ-SD2CMT2-Reborn

- **TapeMZ by Michal Hucik** - SHARP MZ tape archive/file-format reference and related tooling  
  https://github.com/michalhucik/TapeMZ

- **SHARP MZ-800 Technical Reference Manual** - memory map, hardware, ROM routines and machine-level behavior  
  https://www.radeksuk.cz/sharp/gdg/dokumentace/MZ800_Technical_reference_manual.pdf

- **Direct Z80/ROM analysis of the SHARP MZ-800 1Z-013B and MZ-700 tape routines** - used to verify NORMAL and MZ700 pulse timing and contextual pulse widths.

- **Intercopy V10.2** - loader/writer code and timing behavior used as a reference for IC and accelerated NORMAL tape profiles.

- **Turbo Copy V1.22** - loader/writer code and timer behavior used as a reference for TC profiles.

- **FlashFloppy Quick Disk documentation** - QuickDisk hardware/image behavior and FlashFloppy QuickDisk compatibility  
  https://github.com/keirf/flashfloppy/wiki/Quick-Disk

- **HxC Floppy Emulator project** - HxC image/container and QuickDisk implementation reference  
  https://github.com/jfdelnero/HxCFloppyEmulator

- **NAudio.SoundFile / libsndfile** - FLAC decoding used by the Advanced audio import path  
  https://www.nuget.org/packages/NAudio.SoundFile/  
  https://libsndfile.github.io/
