# QDTool – Codex zadání: podpora `.QD` obrazů Sharp legacy + HxC + FlashFloppy

Repozitář: `bales0/QDTool`

## Cíl

Rozšiř QDTool o plnou podporu tří variant souborů s příponou `.qd`:

1. **Sharp/MZ legacy logical QD image** – starý cca 61 kB obraz používaný emulátory/MZQDTool.
2. **HxC QuickDisk image** – fyzický/raw-bitcell container s hlavičkou `HXCQDDRV`.
3. **FlashFloppy QuickDisk image** – zjednodušená HxC-kompatibilní varianta s 8B signaturou obsahující `QD` na pozicích 3–4.

Podpora musí zahrnovat:

- Open,
- Add,
- Save / Save As,
- automatickou detekci konkrétní `.qd` varianty při čtení,
- převod všech podporovaných QD variant do společného interního seznamu Sharp MZF/QD records,
- zápis zpět do zvolené `.qd` varianty,
- regresní testy proti dodaným čtyřem referenčním obrazům.

Neprováděj commit ani push. Změny ponech pouze v pracovním stromu a na konci vypiš změněné soubory a výsledky testů.

---

# 1. Referenční soubory – považuj je za golden fixtures

K dispozici jsou čtyři konkrétní soubory. Přidej je do testovacího workflow jako referenční fixtures; neměň jejich obsah.

## 1.1 `Old_MZ700.qd`

Velikost:

```text
61455 B = 0xF00F
```

SHA-256 začíná:

```text
96dee87024dbf22e
```

Začátek:

```text
00 16 16 A5 00 43 52 43
00 16 16 A5 55 AA 55 AA 55 AA ...
```

Struktura prázdného obrazu:

```text
offset 0x0000:
00 16 16 A5
00
43 52 43          // "CRC"

offset 0x0008:
00 16 16 A5
55 AA 55 AA ...   // formátovací/check pattern

konec:
... 55 AA 55
43 52 43
00
```

Výskyty:

```text
00 16 16 A5 : offset 0 a 8
"CRC"       : offset 5 a 61451
```

Jde o **logický Sharp QD image**, nikoliv HxC/FlashFloppy raw-bitcell image.

Prvních 8 B je stejná logická hlavička, jakou QDTool již používá pro `.mzq`.

Prázdný obraz má:

```text
FileBlocksCount = 0
```

Po tomto logical headeru následuje formátovací zbytek média.

---

## 1.2 `Flashfloppy_Blank.qd`

Velikost:

```text
204800 B = 0x32000
```

SHA-256 začíná:

```text
f5acdf4f50c870a1
```

Prvních 8 B:

```text
00 00 00 51 44 00 00 00
         Q  D
```

To odpovídá FlashFloppy QD signatuře:

```text
byte[3] == 'Q'
byte[4] == 'D'
```

První track descriptor je na offsetu:

```text
0x200 = 512
```

a obsahuje 4× `uint32 little-endian`:

```text
offset    = 0x00000400 = 1024
len       = 0x00031A99 = 203417
win_start = 0x000031A9 = 12713
win_end   = 0x0002B745 = 177989
```

Track data začínají na:

```text
0x400
```

Soubor je zarovnán/padded do:

```text
0x32000
```

a blank raw track je zde vyplněn hodnotou:

```text
0x11
```

Pozor: descriptor `len` je menší než fyzicky uložený 512B-rounded prostor. Reader musí respektovat descriptor `len`, nikoliv slepě celý zbytek souboru.

---

## 1.3 `HxC_DSKA0000_Blank.QD`

Velikost:

```text
204800 B = 0x32000
```

SHA-256 začíná:

```text
ae0b1383154ca423
```

HxC header:

```text
"HXCQDDRV"
revision          = 0
number_of_track   = 1
number_of_side    = 1
track_encoding    = 0
write_protected   = 0
bitRate           = 203389
flags             = 0
track_list_offset = 512
```

Track descriptor na `0x200`:

```text
offset            = 0x400
track_len         = 0x31C00 = 203776
start_sw_position = 0x3200  = 12800
stop_sw_position  = 0x25600 = 153088
```

Celý blank track je v tomto konkrétním fixture vyplněn:

```text
0x01
```

---

## 1.4 `hxc_Mario.qd`

Velikost:

```text
204800 B = 0x32000
```

SHA-256 začíná:

```text
71244b7b7f833822
```

HxC header:

```text
"HXCQDDRV"
revision          = 0
number_of_track   = 1
number_of_side    = 1
track_encoding    = 0
write_protected   = 0
bitRate           = 203388
flags             = 0
track_list_offset = 512
```

Track descriptor:

```text
offset            = 0x400
track_len         = 0x31C00
start_sw_position = 0x3200
stop_sw_position  = 0x25600
```

Tento obraz obsahuje jeden platný Sharp MZ soubor.

Po MFM decode jsou nalezeny tři validní Sharp QD frames:

### FNBLK

```text
A5
02
FA 91    // CRC, validní podle existující QDTool CRC logiky
```

`02` znamená dva file blocks = jeden header + jeden body = jeden soubor.

### File header frame

```text
A5
00          // header block
40 00       // DataSize = 64
...64 B...
0F 72       // valid CRC
```

Dekódovaná Sharp data dávají:

```text
Ftype       = 0x01
DisplayName = "MARIO SPECIAL"
MZF size    = 0xA8C2 = 43202 B
Load        = 0x1200
Exec        = 0x1200
```

Poznámka: filename field obsahuje data i za prvním `0x0D`; současné QDTool zobrazení správně končí na prvním CR. Nepřepisuj RAW filename/header data podle zobrazovaného stringu.

### File body frame

```text
A5
05
C2 A8       // 43202 B
<body>
D3 5C       // valid CRC
```

První bajty body:

```text
F3 31 00 D0 D3 E6 D3 E0 D3 E3 3E 01 D3 F0 21 00
D0 11 01 D0 01 E7 03 36 00 ED B0 21 00 D8 11 01
```

Posledních 32 B body:

```text
18 18 00 00 30 0C 00 00 30 0C 00 00 30 0C 00 00
18 18 00 00 0F F0 00 00 00 00 00 00 00 00 00 FF
```

Tento fixture je povinný golden test MFM decoderu.

---

# 2. Veřejné definice formátu, které musí implementace respektovat

Při implementaci vycházej z:

- HxC Floppy Emulator:
  - `libhxcfe/sources/loaders/qd_loader/qd_format.h`
  - `qd_loader.c`
  - `qd_writer.c`
- FlashFloppy:
  - `src/image/qd.c`
  - `scripts/mk_qd.py`
- dokumentace Sharp QD:
  - „Inside the Quick Disk“ – Bernd Krueger-Knauber
- současné implementace QDTool:
  - `MZQFileReader.cs`
  - `QDFFileReader.cs`
  - `Utility.cs`
  - `MainWindow.xaml.cs`

Nevkládej do projektu externí HxC nebo FlashFloppy runtime knihovnu. Implementuj potřebný parser/encoder přímo v C#/.NET a znovupoužij současnou QDTool CRC logiku.

---

# 3. Základní architektonické pravidlo

Přípona `.qd` NESMÍ určovat parser.

Použij content-based autodetection.

Navržené interní rozdělení:

```text
QdFormatDetector
    ├─ SharpLegacyLogical
    ├─ HxcPhysical
    ├─ FlashFloppyPhysical
    └─ Unknown

SharpLegacyQdCodec

HxcFlashFloppyQdContainer
    ├─ parse full HxC header
    ├─ parse simplified FlashFloppy header
    ├─ parse track descriptor
    ├─ extract raw bitcell track
    └─ write requested physical container variant

QuickDiskMfmCodec
    ├─ DecodeSharpFrames(...)
    └─ EncodeSharpFrames(...)

SharpQdFrameCodec
    ├─ FNBLK
    ├─ MZF/QD header block
    ├─ MZF/QD body block
    ├─ sync/break/gap handling
    └─ CRC

QDTool common MZF/record model
```

Nedělej dva téměř identické raw-track decodery pro HxC a FlashFloppy. Rozdíl HxC/FF je hlavně container/header/timing metadata; vlastní track je stejný typ raw bitcell dat.

---

# 4. Autodetekce `.qd`

Použij přibližně toto pořadí:

## 4.1 HxC canonical

Pokud:

```text
bytes[0..7] == "HXCQDDRV"
```

=> `HxcPhysical`

Dále validuj HxC header a track descriptor.

## 4.2 FlashFloppy / HxC-compatible simplified

Pokud:

```text
bytes[3] == 'Q'
bytes[4] == 'D'
```

a nejde již o canonical `HXCQDDRV`:

=> `FlashFloppyPhysical`

Dále validuj descriptor na offsetu 512.

## 4.3 Sharp legacy logical

Pokud začátek odpovídá:

```text
00 16 16 A5 ?? 43 52 43
```

=> `SharpLegacyLogical`

Pro `Old_MZ700.qd` navíc očekávej typickou velikost `0xF00F`, ale velikost sama nesmí být jedinou detekcí.

## 4.4 Unknown

Jinak `.qd` odmítni jako neznámý QD image.

---

# 5. Povinná validace HxC/FlashFloppy containeru

Všechny hodnoty jsou little-endian.

Validuj minimálně:

```text
track descriptor offset existuje
track data offset >= 0x400 pro naše standardní single-track obrazy
track_len > 0
track_offset + track_len <= file length
win_start <= win_end
win_end <= track_len
```

U canonical HxC:

```text
number_of_track >= 1
number_of_side >= 1
track_list_offset je v souboru
track_list_offset + descriptor table se vejde do souboru
```

QDTool pro Sharp MZ potřebuje pouze jeden QuickDisk track/side.

Pokud container obsahuje více tracků/sides, neinterpretuj je potichu jako Sharp MZ. Buď:

- podporuj pouze první single-track/side variantu, pokud je formát explicitně 1×1,
- nebo zobraz jasnou chybu „unsupported QuickDisk geometry“.

---

# 6. HxC/FlashFloppy raw track – bit order

HxC definice výslovně říká, že bity každého uloženého track byte se odesílají:

```text
bit 0 -> bit 7
```

FlashFloppy pracuje se stejnou raw bitcell reprezentací.

Decoder tedy nesmí automaticky interpretovat bytes MSB-first.

Při převodu na bitstream použij:

```text
byte:
b0, b1, b2, b3, b4, b5, b6, b7
```

---

# 7. MFM decoder – zásadní požadavek z `hxc_Mario.qd`

NESPOLÉHEJ na jeden globální byte/bit phase offset pro celý track.

Analýza `hxc_Mario.qd` ukazuje, že tři validní Sharp frames lze spolehlivě nalézt při různých 16-cell alignment kandidátech.

Proto robustní decoder musí hledat Sharp frame strukturu na bitcell úrovni.

Doporučená implementace:

1. převést raw track na LSB-first bitcell stream,
2. vyzkoušet všech 16 možných alignmentů jednoho MFM byte (`16 bitcells = 8 clock + 8 data cells`),
3. pro každý alignment dekódovat candidate data stream,
4. hledat Sharp QD sync/frame sequence,
5. validovat kandidáta pomocí:
   - start/sync struktury,
   - typu bloku,
   - délky,
   - dostupnosti kompletního bloku,
   - CRC,
6. převést nalezený frame zpět na absolutní raw bit/byte position,
7. kandidáty z různých alignmentů seřadit podle fyzické pozice,
8. odstranit duplicity,
9. sestavit pouze konzistentní Sharp frame sequence.

Neakceptuj frame jen proto, že někde v raw datech náhodně vzniklo `A5`.

CRC je rozhodující validace.

---

# 8. Sharp frame synchronizace

Na fyzickém QD před daty existuje break/sync oblast.

QDTool již v QDF používá logiku:

```text
00
16 16 ... minimálně 2×
A5
<data>
CRC16
```

Při hledání frame nepředpokládej přesný počet `0x16`; požaduj minimálně dva a validuj CRC.

Na writeru můžeš použít současné QDF počty sync/gap bytes jako výchozí Sharp profil, ale odděl je do společného codec/profile kódu – neduplikuj magic numbers v několika writerech.

---

# 9. CRC

Použij existující `Utility.CRC_check()` semantiku.

Nepiš druhou „podobnou“ CRC implementaci, pokud není nutná.

Golden sample `hxc_Mario.qd` musí dát:

```text
FNBLK CRC       -> valid
Header CRC      -> valid
Body CRC        -> valid
```

Konkrétně nalezené CRC bytes jsou:

```text
FNBLK : FA 91
Header: 0F 72
Body  : D3 5C
```

---

# 10. Parsování Sharp physical image

Po dekódování tracku očekávej:

```text
FNBLK
N file blocks
```

Pro naše QDTool struktury musí být block count sudý:

```text
fileCount = FNBLK / 2
```

Každý soubor:

```text
header frame
body frame
```

Použij stejnou logickou interpretaci jako současné QDF/MZQ.

Pro `hxc_Mario.qd`:

```text
FNBLK = 2
=> 1 file
```

---

# 11. Empty physical QD images

`Flashfloppy_Blank.qd` ani `HxC_DSKA0000_Blank.QD` neobsahují Sharp FNBLK frame.

Přitom jde o validní prázdné QD images.

Reader proto nesmí:

```text
valid HxC/FF container + no Sharp frame
=> vždy error
```

Rozliš:

## Zjevně blank track

Například track je prakticky/uniformně blank filler:

```text
HxC fixture: 0x01
FF fixture : 0x11
```

=> načti jako validní prázdný QuickDisk s 0 records.

Nehardcoduj pouze tyto dva bytes jako jedinou možnou definici blank media; vytvoř rozumnou `IsBlankTrack` detekci.

## Nonblank track bez validního Sharp layoutu

Pokud raw track obsahuje významná data, ale nelze sestavit validní Sharp FNBLK/header/body sequence:

zobraz chybu ve smyslu:

```text
The QD container is valid, but it does not contain a supported Sharp MZ QuickDisk image.
```

HxC `.qd` je obecný QuickDisk container a může obsahovat Roland, Akai, MO5 atd. QDTool nesmí takový image chybně interpretovat jako Sharp.

---

# 12. Mapování QD headeru do MZF

QuickDisk header obsahuje pouze 64 B Sharp header dat a z původního MZF description se zachovává jen prvních 38 B.

Současné QDTool `MZQFileReader`/`QDFFileReader` už tuto logiku mají.

Při převodu QD -> interní MZF:

- zachovej všech 38 dostupných description bytes byte-for-byte,
- zbývajících 66 B MZF description nastav na nulu,
- neměň binární obsah prvních 38 B podle textového UI.

To je důležité kvůli header-only loaderům a historickým binárním datům.

---

# 13. Refaktor QDF – nevytvářej třetí Sharp frame parser

Současný `QDFFileReader` už řeší:

- hledání sync sequence,
- FNBLK,
- header/data blocky,
- CRC,
- tvorbu `MZQFileHeader`/`MZQFileBody`.

Vyčleň z něj společný `SharpQdFrameCodec` nebo ekvivalent.

Cíl:

```text
QDF
  -> Sharp byte/frame layer
  -> common records

HxC/FF
  -> raw bitcells
  -> MFM decode
  -> Sharp byte/frame layer
  -> common records
```

A opačně:

```text
common records
  -> Sharp frame layer
  -> QDF

common records
  -> Sharp frame layer
  -> MFM encode
  -> HxC/FF container
```

Nevytvářej samostatnou kopii QDF CRC/frame parseru uvnitř HxC readeru.

---

# 14. Legacy `Old_MZ700.qd`

Tento formát je logický, nikoliv MFM.

Reader může maximálně znovupoužít MZQ logiku.

Rozdíl:

```text
MZQ:
[logical header][file blocks] EOF

legacy QD:
[logical header][file blocks][remaining formatted media/check tail]
```

Pro reader:

1. přečti prvních 8 B stejně jako MZQ,
2. `FileBlocksCount` určuje počet skutečných file blocks,
3. načti přesně tyto file blocks,
4. zbytek je formatted-media area, ne další soubory,
5. nezkoušej v `55 AA` patternu hledat další fake MZF headers.

Prázdný `Old_MZ700.qd` musí vrátit 0 records bez chyby.

---

# 15. Legacy `.qd` writer

Výstup musí mít:

```text
61455 B = 0xF00F
```

Začátek je MZQ-compatible logical header:

```text
00 16 16 A5
FileBlocksCount
43 52 43
```

Poté zapiš všechny file header/body blocks stejným logical framingem jako MZQ.

Zbývající prostor doplň jako formatted/check area tak, aby obraz zůstal pevné délky.

Pro prázdný disk musí writer přesně vytvořit golden strukturu `Old_MZ700.qd`:

```text
00 16 16 A5 00 43 52 43
00 16 16 A5
55 AA 55 AA ...
43 52 43 00
```

U neprázdného image začni zbývající formatting tail bezprostředně po posledním logickém file blocku a vyplň jej do pevné velikosti.

Před zápisem proveď capacity check.

Pokud se records + formatting terminator nevejdou:

```text
Cannot save: QuickDisk capacity exceeded.
```

Nepřeteč a netruncuj file body.

---

# 16. Physical HxC/FlashFloppy writer – společná data, různé profily

Implementuj jeden společný raw-track builder a dva container/output profiles.

## 16.1 HxC Sharp profile

Pro nový HxC output použij Sharp-specific profil podle dodaného funkčního sample, ne obecný floppy profil:

```text
signature          = "HXCQDDRV"
revision           = 0
tracks             = 1
sides              = 1
track_encoding     = 0
write_protected    = 0
flags              = 0
track_list_offset  = 0x200
track_data_offset  = 0x400
track_len          = 0x31C00
start_sw_position  = 0x3200
stop_sw_position   = 0x25600
file size          = 0x32000
```

`hxc_Mario.qd` používá:

```text
bitRate = 203388
```

blank fixture používá 203389.

Reader musí akceptovat obě hodnoty.

Pro nový Sharp-HxC image použij jednu jasně definovanou konstantu/profile hodnotu (preferuj hodnotu z obsahového golden sample, tj. 203388), nikoliv náhodné zaokrouhlení v různých částech kódu.

Při obyčejném Save již otevřeného HxC image je vhodné zachovat kompatibilní source metadata, pokud nejsou v konfliktu s novým obsahem.

## 16.2 FlashFloppy Sharp profile

Použij formát odpovídající dodanému `Flashfloppy_Blank.qd` a oficiálnímu `mk_qd.py` profilu:

```text
8B signature:
00 00 00 51 44 00 00 00

descriptor offset  = 0x200
track data offset  = 0x400
track_len          = 0x31A99
win_start          = 0x31A9
win_end            = 0x2B745
file size          = 0x32000
```

Raw storage je 512B-rounded, ale descriptor `track_len` zůstává `0x31A99`.

Blank filler profilu:

```text
0x11
```

---

# 17. MFM encoder

Implementuj standardní MFM cell encoding.

Důležité:

- výsledné QD track bytes se ukládají tak, že bitcell stream je po bytech LSB-first,
- zachovej správný previous-data-bit state během jednoho souvislého MFM segmentu,
- kde fyzický Sharp layout obsahuje break/gap/reset, modeluj jej explicitně,
- nevytvářej track pouhým bit-reverse celé QDF file bez pochopení hranic.

Vytvoř samostatné unit testy:

```text
data byte -> MFM cells -> decode -> stejný data byte
```

pro:

```text
00
16
A5
55
AA
FF
náhodná data
```

a test přes delší buffer.

---

# 18. Physical Sharp track builder

Nepřeváděj slepě celý fixní `81920 B` QDF payload na MFM.

Důvod:

- QDF obsahuje container-specific leading/trailing padding,
- HxC Sharp ready/data window v golden sample je kratší,
- fyzický QD track má vlastní lead-in, data window a lead-out.

Vyčleň významnou Sharp frame stream logiku z QDF writeru a vytvoř fyzický track přibližně takto:

```text
lead-in / blank filler
start switch position
FNBLK frame
inter-frame gap
header frame
gap
body frame
gap
další header/body...
unused data-window area
lead-out / blank filler
```

Použij současné QDF frame/gap semantics jako zdroj Sharp timing/layout pravidel, ale nenechávej QDF outer signature/padding proniknout do HxC tracku.

Všechny kompletní Sharp frames se musí vejít do aktivní fyzické oblasti.

Pokud se nevejdou do zvoleného HxC/FF profilu:

```text
Cannot save: QuickDisk physical data window capacity exceeded.
```

---

# 19. Round-trip požadavek

Nepožaduj byte-identical HxC/FF output proti vstupu.

Raw physical obraz může být logicky ekvivalentní i při jiném legálním filler/gap patternu.

Povinné je:

```text
records
 -> physical QD writer
 -> physical QD reader
 -> records
```

a výsledné records musí být logicky identické:

- ftype,
- raw filename bytes,
- size,
- load,
- exec,
- prvních 38 description bytes,
- body byte-for-byte.

CRC musí být validní.

---

# 20. Dokumentový stav / zachování varianty

Přidej explicitní informaci o zdrojovém formátu dokumentu.

Například:

```text
DocumentFormat:
    MZQ
    QDF
    MZT
    MZF
    QD_SharpLegacy
    QD_HxC
    QD_FlashFloppy
```

Nepoužívej pouze extension.

Je to nutné, protože všechny tři nové varianty používají `.qd`.

Při otevření:

```text
game.qd
```

si aplikace musí pamatovat, kterou variantu autodetekovala.

---

# 21. Open / Add UI

Do společného podporovaného filtru přidej:

```text
*.qd
```

Pro `.qd` zobraz v Open/Add pouze jednu uživatelskou položku, například:

```text
QuickDisk image (*.qd)|*.qd
```

Uživatel při čtení nemá ručně vybírat variantu.

Variantou se zabývá autodetektor.

Basic workflow musí zůstat jednoduchý.

---

# 22. Save As UI

Protože stejná extension znamená tři různé výstupní formáty, samotné:

```text
*.qd
```

nestačí k výběru writeru.

Přidej tři odlišné Save As filter entries, například:

```text
QuickDisk - HxC (*.qd)
QuickDisk - FlashFloppy (*.qd)
QuickDisk - Sharp/MZ legacy (*.qd)
```

Všechny používají `.qd`, ale writer se vybírá podle `FilterIndex` / explicitního output format enumu, ne podle extension.

Následující kód je zakázaná logika:

```csharp
if (extension == ".qd")
    SaveOneSpecificQdVariant();
```

Musí existovat explicitní output variant.

---

# 23. Save existujícího `.qd`

Pokud aktuální dokument vznikl otevřením `.qd`, normální Save má zachovat jeho variantu:

```text
QD_HxC          -> HxC
QD_FlashFloppy  -> FlashFloppy
QD_SharpLegacy  -> legacy
```

Pokud současná UI implementace fakticky vždy používá SaveFileDialog, alespoň předvol správný filter index podle uloženého `DocumentFormat`.

Neodvozuj variantu z názvu souboru.

---

# 24. Basic / Advanced kompatibilita

Tyto `.qd` formáty jsou core funkce QDToolu, proto je v Basic režimu NESKRÝVEJ.

Basic uživatel nemusí vidět:

- MFM,
- raw bitcells,
- bitrate,
- track_len,
- switch positions,
- HxC interní metadata.

V Basic pouze:

```text
QuickDisk image
```

a případně při Save As lidsky srozumitelné rozlišení HxC / FlashFloppy / legacy.

Pokud již existuje Advanced režim, fyzické diagnostické údaje mohou být pouze tam.

Pokud Advanced ještě v aktuální větvi není, nezaváděj kvůli tomuto úkolu velký nový UI refaktor. Kód ale strukturuj tak, aby se fyzická metadata dala později zobrazit.

---

# 25. Chování trailing dat MZF

QD formáty ukládají vlastní 64B QD header + declared body.

Standalone MZF trailing data se do QD nepřenášejí.

Při převodu:

```text
MZF with trailing -> any QD
```

zapisuj pouze declared MZF body.

Nepokoušej se trailing vměstnat do QD image.

Respektuj současná/plánovaná Basic/Advanced pravidla QDToolu pro trailing, ale samotný QD writer trailing ignoruje.

---

# 26. Capacity validation

Rozšiř `TryValidateOutputFormat` nebo jeho budoucí ekvivalent o QD varianty.

Nekalkuluj všechny QD formáty jednou konstantou.

Každý má jinou reprezentaci:

```text
MZQ                 logical size
QDF                 fixed QDF image
QD_SharpLegacy      fixed 0xF00F logical image
QD_HxC              physical MFM track/window
QD_FlashFloppy      physical MFM track/window
```

Pro physical QD:

1. nejdřív sestav/odhadni encoded Sharp frame bitcell length,
2. zkontroluj, že se vejde do data window,
3. teprve poté dovol Save.

---

# 27. Error handling

Rozlišuj chyby:

```text
Unknown .QD format
Invalid HxC/FlashFloppy container
Invalid track descriptor
Unsupported QD geometry
Valid QD container but unsupported non-Sharp contents
Corrupt Sharp QD frame
CRC error
FNBLK/header/body inconsistency
QuickDisk capacity exceeded
```

Neschovávej všechny pod obecné:

```text
Invalid file
```

---

# 28. Povinné testy – format detector

Golden:

```text
Old_MZ700.qd
=> QD_SharpLegacy

HxC_DSKA0000_Blank.QD
=> QD_HxC

hxc_Mario.qd
=> QD_HxC

Flashfloppy_Blank.qd
=> QD_FlashFloppy
```

Testuj také:

- random `.qd`,
- zkrácený HxC header,
- descriptor mimo file bounds,
- `win_end > track_len`.

---

# 29. Povinné testy – blank images

## Old legacy blank

Open:

```text
0 records
```

Save As legacy:

```text
size = 0xF00F
```

a prázdný output musí mít stejnou definovanou strukturu jako golden fixture.

## HxC blank

Open:

```text
0 records
no error
```

## FlashFloppy blank

Open:

```text
0 records
no error
```

---

# 30. Povinný golden test – `hxc_Mario.qd`

Otevření musí dát přesně:

```text
record count = 1
Ftype        = 0x01
display name = "MARIO SPECIAL"
size         = 43202
load         = 0x1200
exec         = 0x1200
body length  = 43202
```

Body první a poslední bytes porovnej s hodnotami uvedenými výše.

Ověř všechny tři CRC frames.

Test musí prokázat, že decoder není závislý na jednom hardcoded globálním MFM alignmentu.

---

# 31. Povinné cross-format round-trip testy

Minimálně:

```text
hxc_Mario.qd
 -> records
 -> QDF
 -> records
```

```text
hxc_Mario.qd
 -> records
 -> MZQ
 -> records
```

```text
hxc_Mario.qd
 -> records
 -> HxC QD
 -> records
```

```text
hxc_Mario.qd
 -> records
 -> FlashFloppy QD
 -> records
```

```text
MZF
 -> HxC QD
 -> MZF logical data
```

```text
MZF
 -> FlashFloppy QD
 -> MZF logical data
```

```text
MZF
 -> legacy QD
 -> MZF logical data
```

Pro MZF/QD převod porovnávej pouze data, která QD formát umí reprezentovat; QD zachovává jen prvních 38 B description.

---

# 32. MFM unit tests

Samostatně otestuj encoder/decoder.

Požaduj například:

```text
Encode(Decode(x))
```

nebo ekvivalentní logical round-trip pro:

```text
00
16
A5
55
AA
FF
00 16 16 A5
random blocks
```

Dále testuj několik různých počátečních bitcell alignmentů.

Decoder musí umět najít validní frame i při posunu raw bitstreamu.

---

# 33. Nepřepisuj současné funkce QDToolu od nuly

Cílem je rozšíření, nikoliv rewrite.

Zachovej:

- současné MZQ chování,
- QDF podporu,
- MZF/MZT podporu,
- současné opravy validace,
- export MZF,
- UI chování mimo nutné změny.

Refaktor QDF/MZQ pouze tam, kde odstraní duplicitu a umožní bezpečně sdílet Sharp QD logiku.

---

# 34. Nezaváděj filesystem temp-file pipeline

Zakázaný návrh:

```text
HxC -> temp.qdf -> znovu otevřít temp.qdf
```

nebo:

```text
records -> temp.qdf -> externí converter -> qd
```

Všechno realizuj přes `Stream` / `byte[]` / interní codec vrstvy.

---

# 35. Žádný externí proces

Nespouštěj:

- HxC command-line tool,
- Python converter,
- FlashFloppy script,
- jiný externí executable.

QDTool musí `.qd` číst a zapisovat sám.

---

# 36. Doporučené nové třídy

Názvy mohou být jiné, ale odpovědnosti musí být oddělené:

```text
QdImageFormat.cs
QdFormatDetector.cs

SharpQdFrameCodec.cs
SharpLegacyQdReaderWriter.cs

HxcFlashFloppyQdContainer.cs
QuickDiskMfmCodec.cs
QuickDiskPhysicalReader.cs
QuickDiskPhysicalWriter.cs

QuickDiskPhysicalProfile.cs
```

Nedávej vše do dalšího obřího `MainWindow.xaml.cs`.

---

# 37. Důležitý detail: HxC a FlashFloppy nejsou samy o sobě „Sharp format“

HxC/FF `.qd` je generic physical QuickDisk container.

Proto:

```text
valid HXCQDDRV
```

neznamená:

```text
valid Sharp MZ image
```

Sharp validitu prokazuje až:

- validní Sharp frame sequence,
- FNBLK,
- header/body pairing,
- délky,
- CRC.

Blank track je zvláštní povolený případ.

---

# 38. Důležitý detail: nepoužívej pouze raw byte pattern search

Raw physical track je MFM.

Nehledej přímo:

```text
00 16 16 A5
```

v raw HxC bytes.

Nejdřív je nutné dekódovat MFM bitcells.

Naopak legacy `Old_MZ700.qd` je logical byte image a MFM decode se na něj NESMÍ aplikovat.

---

# 39. Výsledek Save As musí být deterministický

Pro stejné records a stejný zvolený output profile má writer vytvořit deterministický image.

To usnadní:

- testování,
- diff,
- archivaci,
- reprodukovatelnost.

Nevkládej timestamps nebo náhodná data do QD image.

---

# 40. Po dokončení vypiš

Na konci práce uveď:

1. všechny změněné soubory,
2. přidané třídy,
3. přesný autodetection algoritmus,
4. strukturu legacy QD readeru/writeru,
5. HxC header/descriptor parser,
6. FlashFloppy header/descriptor parser,
7. jak funguje MFM decode,
8. jak řešíš více možných 16-cell alignmentů,
9. jak funguje MFM encode,
10. jak vzniká Sharp physical track,
11. jak se provádí capacity validation,
12. jak Save As rozlišuje tři `.qd` writery se stejnou extension,
13. výsledky všech golden testů,
14. výsledky cross-format round-trip testů,
15. případná omezení, která ještě zůstala.

Neprováděj commit ani push.

---

# Akceptační kritéria

Úkol není hotový, dokud neplatí současně:

- `Old_MZ700.qd` je rozpoznán jako legacy Sharp QD a otevře se jako prázdný.
- `HxC_DSKA0000_Blank.QD` je rozpoznán jako HxC a otevře se jako prázdný.
- `Flashfloppy_Blank.qd` je rozpoznán jako FlashFloppy a otevře se jako prázdný.
- `hxc_Mario.qd` je rozpoznán jako HxC a vrátí přesně jeden soubor `MARIO SPECIAL`, 43202 B, load/exec `0x1200`.
- CRC count/header/body z `hxc_Mario.qd` projdou.
- QDTool umí zapsat legacy `.qd`.
- QDTool umí zapsat canonical HxC `.qd`.
- QDTool umí zapsat FlashFloppy `.qd`.
- Všechny tři writery lze znovu otevřít vlastním readerem bez ztráty reprezentovatelných Sharp dat.
- `.qd` read autodetection není založena pouze na extension.
- HxC/FF decoder není založen na jednom hardcoded MFM phase offsetu.
- HxC/FF generic non-Sharp image není chybně načten jako Sharp.
- Existující MZQ/QDF/MZF/MZT funkce zůstanou regresně funkční.
