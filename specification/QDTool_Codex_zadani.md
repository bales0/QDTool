# QDTool – implementační zadání

Repozitář: `bales0/QDTool`

Cílem je upravit současný QDTool tak, aby:

1. **Basic režim co nejpřesněji zachoval původní filozofii a funkce QDToolu**, pouze s již provedenými nebo nutnými opravami chyb v datech, parsování a grafice.
2. Nové nebo technicky pokročilé funkce byly dostupné pouze v **Advanced režimu**.
3. Práce s MZF/MZT byla datově korektní a nedocházelo k nechtěnému poškození historických MZF souborů.
4. Podpora SD2CMT2 `MFI/MTI` byla pro běžného uživatele Basic režimu zcela transparentní.
5. Interní model byl připraven na pozdější podporu WAV, L16, LEP, TMZ a dalších tape formátů, aniž by se tyto funkce vnucovaly Basic režimu.

Neprováděj commit ani push. Změny ponech v pracovním stromu a na konci popiš změněné soubory, provedené testy a případné otevřené otázky.

---

# 1. Nejdříve analyzuj současný stav

Před změnami projdi aktuální zdrojový kód a zjisti:

- současnou strukturu `MainWindow`,
- aktuální datový model MZF/MZT,
- `MZTFileReader`,
- MZF/MZT writery,
- Open/Add/Save/Save As dialogy,
- aktuální hex/header viewer,
- současnou implementaci `Truncate`,
- případnou již existující podporu dalších formátů,
- `AboutDialog.xaml`,
- `QDTool.csproj`,
- všechny již provedené opravy grafiky a parsování.

Nevracej staré chyby jen proto, aby se Basic podobal původní verzi. Basic má zachovat **funkční chování originálu**, nikoliv známé chyby.

Pokud je současná implementace již v některých bodech lepší než níže popsaný návrh, zachovej lepší řešení, pokud respektuje zde definované chování.

---

# 2. Basic a Advanced režim

Přidej uživatelské nastavení:

`Enable advanced features`

Výchozí hodnota musí být vypnutá.

Přepnutí Basic/Advanced musí fungovat za běhu bez restartu aplikace.

Použij pokud možno jeden společný layout a měň `Visibility`, dostupné sloupce, panely, položky menu a filtry dialogů.

## Zásadní pravidlo

Přepnutí Basic ↔ Advanced:

- nesmí měnit data projektu,
- nesmí mazat metadata,
- nesmí měnit pořadí záznamů,
- nesmí automaticky konvertovat soubory,
- mění pouze dostupné UI a pravidla následných explicitních operací uživatele.

---

# 3. Basic režim

Basic má působit jako původní jednoduchý QDTool.

Běžný uživatel nemá potřebovat vědět nic o:

- trailing datech,
- MFI,
- MTI,
- tape profilech,
- SD2CMT2,
- WAV analýze,
- LEP,
- L16,
- TMZ,
- pulse datech,
- fyzické reprezentaci pásky.

Technické sloupce, tlačítka a položky menu proto v Basic režimu skutečně **skryj**, ne pouze zašedni.

Formáty jako WAV/L16/LEP/TMZ nesmějí být v Basic režimu viditelné v Add/Open/Save filtrech.

Pokud už je jejich část v aktuálním kódu implementovaná, nemaž ji. Pouze ji zpřístupni až v Advanced režimu.

---

# 4. MZF struktura – zachování RAW headeru

MZF má 128B header.

Oblast `0x18–0x7F` má délku 104 B a bývá interpretována jako description/comment.

Nesmí se však předpokládat, že jde vždy o text. Existují historické/header-only loadery, které v této oblasti obsahují strojový kód, parametry nebo jiná binární data.

## Povinné pravidlo

RAW obsah této oblasti musí být autoritativně zachován byte-for-byte.

Nedělej z textové interpretace autoritativní datový zdroj.

Doporučená logika:

```text
MZF record
    RawHeader[128]
    structured fields derived from RawHeader
    DescriptionRaw = RawHeader[0x18..0x7F]
    DescriptionText = pouze UI interpretace
    Body
    TrailingData
```

Při změně například filename, size, load address nebo exec address změň pouze příslušné bajty headeru.

Pokud uživatel description vůbec needituje, oblast `0x18–0x7F` musí po roundtripu zůstat naprosto identická.

Nepokoušej se automaticky sanitizovat neprintovatelné znaky v RAW datech.

V Advanced režimu lze později rozlišovat textový description od binárního header-loaderu, ale není dovoleno binární data automaticky přepisovat textem.

---

# 5. Samostatný MZF a trailing data

Samostatný `.MZF` obsahuje přesně jeden logický MZF record:

```text
[128 B header]
[body o délce deklarované v headeru]
[cokoliv dalšího = trailing data]
```

Standalone MZF nikdy neinterpretuj jako kontejner několika MZF záznamů.

Reader pro MZF musí tedy:

1. načíst jeden 128B header,
2. získat deklarovanou velikost body,
3. načíst přesně body,
4. všechna zbývající data uložit jako `TrailingData`.

Trailing data mohou obsahovat historicky zajímavé nebo unikátní informace. Parser je proto nesmí při pouhém otevření fyzicky ničit.

---

# 6. Trailing data v Basic režimu

Basic musí zachovat původní chování QDToolu z hlediska výsledného souboru:

**při ukládání MZF v Basic režimu se trailing data automaticky nezapisují.**

Tedy Basic Save MZF = implicitní truncate.

V Basic:

- trailing není zobrazen,
- není sloupec `Trailing`,
- není checkbox Preserve/Truncate,
- není upozornění na trailing,
- uživatel se s trailing daty vůbec nesetká.

Interně je ale vhodné trailing při načtení zachytit, aby parser byl správný a při přepnutí do Advanced ještě před uložením mohl uživatel trailing prohlédnout.

Po úspěšném Basic Save/Save As MZF musí pracovní stav odpovídat právě uloženému MZF, takže `TrailingData` aktuálního dokumentu bude prázdné.

---

# 7. Trailing data v Advanced režimu

Advanced může zobrazit například `Trailing: 256 B` a umožnit jejich prohlížení ve vieweru.

Pro Save jako MZF nabídni možnost zachování trailing dat.

Preferovaný význam volby je pozitivní:

`Preserve trailing data`

místo matoucího `Truncate`.

Pokud je Preserve vypnuté:

- trailing se nezapíše,
- po úspěšném Save se odstraní i z aktuálního pracovního modelu.

Pokud je zapnuté:

- trailing se zapíše beze změny,
- zůstane v modelu.

Viewer po úspěšném Save vždy musí zobrazovat stav odpovídající uloženému aktuálnímu dokumentu.

Nezobrazuj v hlavním vieweru „historická“ data, která již v právě uloženém aktuálním souboru nejsou.

---

# 8. MZT není FAT-like kontejner

MZT nepovažuj za formát s adresářem/FAT tabulkou a offsety jednotlivých MZF.

MZT je jednoduchá sekvence logických MZF records.

Každý record má:

```text
128 B header
+
body podle size z headeru
```

Další record následuje za koncem předchozího recordu.

Reader musí mít oddělenou logiku:

```text
ReadStandaloneMzf(...)
ReadMzt(...)
ReadMzfRecord(...)
```

Nesmí se používat jedna stejná high-level metoda pro MZF i MZT.

---

# 9. Korektní parsování MZT

Nestačí pouze:

```csharp
while (remaining >= 128)
```

a bez validace považovat následujících 128 B za další record.

Vytvoř robustní `TryReadMzfRecord` nebo ekvivalent.

Další záznam musí být přijat pouze tehdy, pokud:

- je dostupný celý 128B header,
- header je strukturálně přijatelný,
- deklarovaná délka body je konzistentní s dostupnými daty,
- celý record se vejde do MZT.

Nevymýšlej offsety heuristickým scanováním uvnitř dat.

MZT recordy mají následovat deterministicky za sebou.

Pokud na konci zůstanou data, která netvoří korektní další MZF record, zacházej s nimi jako s neplatným/container trailing koncem, nikoliv automaticky jako s dalším MZF.

---

# 10. Trailing uvnitř MZT

Per-record trailing data nejsou v klasickém MZT jednoznačně reprezentovatelná.

Například:

```text
MZF1 header
MZF1 body
256 B trailing
MZF2
```

nemá v MZT standardním způsobem informaci, která by říkala, že 256 B patří ještě k MZF1.

Proto:

**při ukládání MZT se trailing data jednotlivých zdrojových MZF vždy zahodí.**

To platí i v Advanced režimu.

Rozdíl:

- Basic: provede se to transparentně.
- Advanced: může zobrazit, že některé recordy trailing obsahují a že MZT je nebude ukládat.

Po úspěšném Save jako MZT musí být per-record `TrailingData` v aktuálním pracovním modelu odstraněna, protože aktuální dokument je nově uložený MZT a viewer mu musí odpovídat.

---

# 11. Interní model recordu

Současný model založený pouze na:

```csharp
List<(MZQFileHeader, MZQFileBody)>
```

je pro další vývoj příliš úzký.

Refaktoruj opatrně na jeden autoritativní record objekt.

Například:

```text
TapeRecord
    RawHeader / MZF header
    Body
    TrailingData

    TapeProfile
    MetadataOrigin

    případně další informace potřebné pro UI
```

Pořadí v UI musí být pořadím těchto record objektů.

Nepoužívej nezávislé paralelní kolekce, které se mohou při Move Up/Move Down rozjet.

---

# 12. Tape profile a metadata origin

Každý record potřebuje interně profil.

Výchozí profil:

```text
NORMAL 1:1
```

Současně musí být známo, zda jde o implicitní default nebo explicitně načtené metadata.

Například:

```text
Profile = NORMAL 1:1
MetadataOrigin = Implicit
```

není totéž jako:

```text
Profile = NORMAL 1:1
MetadataOrigin = LoadedFromMFI
```

Doporučené stavy:

```text
Implicit
LoadedFromMFI
LoadedFromMTI
CreatedOrModifiedInAdvanced
```

Názvy mohou být jiné, ale význam musí zůstat.

Profil patří k `TapeRecord`, nikoliv k jeho pořadovému číslu.

---

# 13. Move Up / Move Down

Při změně pořadí se musí přesouvat celý record včetně:

- headeru,
- body,
- trailing dat,
- profilu,
- metadata origin,
- budoucích metadata polí.

Nikdy nevaz profil přímo na `RECORD=n`.

`RECORD=n` je pouze výsledek serializace MTI podle aktuálního pořadí.

Příklad:

```text
A  NORMAL 1:1
B  IC 1:3
C  TC 1:2
```

po přesunu C nahoru:

```text
C  TC 1:2
A  NORMAL 1:1
B  IC 1:3
```

MTI se pak serializuje podle tohoto nového pořadí.

---

# 14. MFI / MTI – Basic je načítá, ale nezobrazuje

SD2CMT2 sidecary:

```text
FILE.MZF -> FILE.MFI
FILE.MZT -> FILE.MTI
```

V Basic režimu:

- MFI/MTI nejsou v UI viditelné,
- nejsou ve filtrech,
- nejsou technické sloupce,
- není informace „SD2CMT2“,
- není Generate MFI/MTI checkbox.

Ale pokud sidecar existuje, aplikace ho automaticky načte a aplikuje na interní recordy.

Laický uživatel nemá mít důvod si jeho existence vůbec všimnout.

---

# 15. MFI při Add MZF

Při `Add GAME.MZF` zkontroluj vedle něj `GAME.MFI`.

Pokud existuje a je validní:

- načti profil,
- přiřaď jej importovanému `TapeRecord`,
- nastav metadata origin.

Pokud neexistuje:

```text
NORMAL 1:1
Implicit
```

Po provedeném Add nesmí aplikace potřebovat původní zdrojový MZF/MFI z disku.

Importovaná data musí být kompletně v paměťovém modelu.

---

# 16. MTI při Open/Add MZT

Při otevření MZT zkontroluj stejně pojmenovaný MTI.

Pokud existuje:

- načti jej,
- aplikuj jeho `RECORD=n` profily na recordy MZT.

Při `Add OTHER.MZT` lze jeho MTI použít jako zdroj profilů importovaných records.

Ale:

**sidecar přidaného souboru se nesmí stát sidecarem hlavního aktuálního dokumentu.**

Je rozdíl mezi `MetadataOrigin` recordu a `SidecarBinding` aktuálního dokumentu.

---

# 17. SidecarBinding

Projekt potřebuje vědět, zda aktuální hlavní dokument má odpovídající existující sidecar.

Například:

```text
CurrentDocument:
    C:\Tapes\GAME.MZT

Sidecar:
    C:\Tapes\GAME.MTI
```

SidecarBinding se týká aktuálního uloženého dokumentu, nikoliv souborů, které byly do něj pouze přidány přes Add.

---

# 18. Nejdůležitější Basic pravidlo sidecarů

Použij toto pravidlo:

> Basic nikdy nově nevytváří sidecar, který v cílovém místě neexistuje. Pokud ale odpovídající sidecar v cílovém místě již existuje, vždy jej synchronizuje s právě ukládaným MZF/MZT.

To platí i tehdy, když původně otevřený projekt žádná metadata neměl.

---

# 19. Save / Save As MZT – rozhodovací pravidla

Pro cílové `TARGET.MZT` / `TARGET.MTI` při Basic Save/Save As:

### TARGET.MTI neexistuje

Ulož pouze `TARGET.MZT`.

Nový MTI nevytvářej.

To platí i tehdy, pokud zdrojový projekt měl MTI nebo importované recordy měly MFI metadata.

### TARGET.MTI již existuje

Vždy:

1. ulož nový `TARGET.MZT`,
2. kompletně regeneruj `TARGET.MTI` podle právě uložených records.

To platí bez ohledu na to, zda `TARGET.MZT` před operací existoval.

Tedy i:

```text
TARGET.MZT neexistuje
TARGET.MTI existuje
```

musí skončit:

```text
nový TARGET.MZT
aktualizovaný TARGET.MTI
```

To je záměrné.

Typický reálný případ: uživatel starý MZT úmyslně smazal, ale starý MTI zůstal. Novou kopii pak uloží pod stejným názvem.

Nesmí vzniknout:

```text
nový MZT
starý nesouvisející MTI
```

protože by mohly nesedět počty MZF, pořadí, rychlosti nebo typy loaderů.

---

# 20. Existující MTI vždy regenerovat celý

Existující MTI nepatchuj podle jeho starého obsahu.

Kompletně jej sestav podle aktuálního pořadí a profilů recordů.

Například starý MTI obsahuje 7 records, nový MZT pouze 4: výsledný MTI musí mít pouze 4 records.

Při změně pořadí musí být `RECORD=n` přegenerováno.

---

# 21. Projekt bez metadat + existující cílový MTI

I toto je validní situace:

```text
nový projekt:
A  implicit NORMAL 1:1
B  implicit NORMAL 1:1
C  implicit NORMAL 1:1
```

cílový `TAPE.MTI` již existuje.

Pak MTI kompletně regeneruj s defaultními profily odpovídajícími aktuálním records.

Nikdy nenechávej staré profily jen proto, že nový projekt žádné explicitní metadata nemá.

---

# 22. Save As pod novým názvem bez sidecaru

Příklad:

```text
zdroj:
GAME.MZT
GAME.MTI
```

uživatel:

```text
Save As BACKUP.MZT
```

a `BACKUP.MTI` neexistuje.

Basic vytvoří pouze `BACKUP.MZT`.

Nikdy automaticky nekopíruj `GAME.MTI` na `BACKUP.MTI`.

Po úspěšném Save As se `BACKUP.MZT` stává aktuálním dokumentem a vazba na starý `GAME.MTI` musí být zrušena.

Následný Save nesmí modifikovat původní `GAME.MTI`.

---

# 23. Interní metadata po Save As bez sidecaru

Pokud je výsledný aktuální MZT uložen bez MTI, pracovní model musí odpovídat tomu, co je skutečně persistentně uloženo.

Metadata, která byla dostupná pouze ze starého MFI/MTI a nebyla do nového cíle uložena, nesmějí být prezentována jako persistentní metadata nového dokumentu.

Po úspěšném Save As bez sidecaru proto:

- zruš sidecar binding,
- explicitní profily, které nejsou součástí nového dokumentu, vrať do odpovídajícího implicitního stavu.

Pokud uživatel následně zapne Advanced, nemá vidět „tajná“ stará metadata, která nový MZT nikde neobsahuje.

---

# 24. MZF/MFI používá stejnou logiku

Pro `GAME.MZF` / `GAME.MFI` platí analogická pravidla.

Basic:

- existující MFI načte,
- MFI nezobrazuje,
- pokud cílový MFI při Save existuje, aktualizuje jej,
- pokud cílový MFI neexistuje, nově ho nevytvoří.

I osiřelý existující cílový MFI musí být při uložení odpovídajícího MZF synchronizován.

---

# 25. Advanced sidecary

Advanced může později explicitně nabízet:

```text
Generate MFI
Generate MTI
```

pro vytvoření nového sidecaru tam, kde ještě neexistuje.

Bezpečnostní pravidlo však zachovej:

**existující cílový sidecar se nesmí ponechat zastaralý vůči právě uloženému hlavnímu souboru.**

Checkbox tedy může řídit vytvoření nového sidecaru, ale nesmí vést k tichému ponechání nekompatibilního existujícího sidecaru.

---

# 26. Add/Open/Save formáty

Basic musí skrývat pokročilé tape formáty.

Minimálně skrýt:

```text
WAV
L16
LEP
TMZ
```

Pokud jsou již některé implementované, pouze je schovej v Basic.

Advanced je může zobrazit.

Neimplementuj v rámci tohoto úkolu celý TapeMZ WAV analyzer ani nové LEP/L16 dekodéry, pokud v aktuální větvi ještě nejsou.

Připrav ale UI/datový model tak, aby je nebylo nutné později znovu architektonicky předělávat.

U Basic Add dialogu zachovej jednoduché podporované QDTool formáty.

Pokud je v aktuální větvi již implementován `.QD` jako schválený core formát, může zůstat v Basic. Pokud není implementován, nepřidávej jej jen kvůli tomuto úkolu.

---

# 27. Save dialog a formát výstupu

Formát zvolený v Save As určuje výstup.

Nepřidávej samostatné globální WAV/LEP/L16 checkboxy.

V budoucím Advanced režimu mají být formátově specifické volby kontextové.

Příklad:

```text
MZF:
    Preserve trailing data
    Generate MFI

MZT:
    Generate MTI

WAV/LEP/L16:
    Generate separate files
```

`Generate separate files` je budoucí Advanced funkce a nemusí být v tomto úkolu implementována, pokud odpovídající writery ještě neexistují.

---

# 28. Viewer

Basic viewer má zůstat jednoduchý.

Typicky může zobrazovat jen to, co původní uživatel očekává:

```text
Header
Body
```

Advanced může rozšířit pohled například na:

```text
Header
Body
Trailing
Raw
```

Pokud je description oblast binární, viewer musí umožnit RAW zobrazení bez její změny.

---

# 29. UI Basic nesmí prozrazovat Advanced interní strukturu

V Basic skryj například:

- trailing,
- truncate/preserve volby,
- profile,
- MFI/MTI,
- metadata origin,
- SD2CMT2,
- waveform,
- pulse informace,
- WAV analysis,
- recovery,
- tolerance,
- timing hodnoty.

Běžný QDTool workflow musí zůstat jednoduchý.

---

# 30. About dialog a verze

Současné ruční skládání textu verze pomocí mnoha XAML `Run` elementů zjednoduš.

Verze aplikace má mít jeden autoritativní zdroj v `.csproj`.

Například:

```xml
<Version>0.2.0-alpha</Version>
<FileVersion>0.2.0.0</FileVersion>
<AssemblyVersion>0.2.0.0</AssemblyVersion>
```

About dialog nesmí mít ručně zapsané jednotlivé části čísla verze pomocí mnoha `<Run>` elementů.

Načti zkompilovanou verzi aplikace programově z assembly/product metadata.

Cílem je, aby při nové verzi stačilo upravit jedno normálně čitelné místo.

Neměň současný target framework pouze kvůli této úpravě.

---

# 31. Chybové stavy při Save a sidecarech

Pokud cílový sidecar existuje a podle výše uvedených pravidel jej musí QDTool synchronizovat, selhání zápisu sidecaru nesmí být ignorováno.

Nesmí aplikace oznámit plně úspěšný Save a současně ponechat známě zastaralý sidecar.

Preferuj bezpečný postup:

1. nejprve vygenerovat nový obsah hlavního souboru a případného sidecaru,
2. ověřit, že jsou serializovatelné,
3. použít dočasné soubory / bezpečné nahrazení tam, kde je to praktické,
4. při chybě zobrazit jednoznačnou chybu,
5. neoznačovat projekt jako korektně uložený, pokud povinná synchronizace sidecaru selhala.

Není nutné vytvářet složitý transakční filesystem framework, ale nesmí dojít k tichému nekonzistentnímu stavu.

---

# 32. Neměnit zdrojové soubory po Add

Po `Open` nebo `Add` nespoléhej na to, že původní soubor zůstane dostupný na disku.

Například:

```text
Add A.MZF + A.MFI
```

musí načíst vše potřebné do modelu.

Pozdější Save MZT nesmí znovu otevírat původní `A.MZF`, aby z něj získal header nebo profil.

Totéž platí pro trailing data.

---

# 33. Testovací scénáře – MZF

Přidej/regresně ověř minimálně:

### MZF bez trailing

```text
128B header + body
```

roundtrip zachová data.

### MZF s trailing 256 B

Reader:

```text
Header
Body
Trailing=256
```

Basic Save:

```text
Header
Body
```

Advanced Save + Preserve:

```text
Header
Body
původních 256 B přesně
```

### Header-only/binary description

Vytvoř fixture, kde oblast `0x18–0x7F` obsahuje ne-ASCII/binární data.

Open + Save bez editace musí tuto oblast zachovat byte-for-byte.

---

# 34. Testovací scénáře – MZT

Ověř:

```text
MZF1
MZF2
MZF3
```

s různými sizes.

Reader musí začátek každého následujícího recordu odvodit z předchozího header+size.

Ověř změnu pořadí.

Ověř MZT vytvořený ze standalone MZF, které mělo trailing:

```text
MZF1 + trailing
MZF2
```

výsledný MZT:

```text
MZF1
MZF2
```

bez vloženého trailing mezi records.

---

# 35. Testovací scénáře – sidecary

Minimálně ověř:

```text
Open MZT bez MTI
Save
=> MTI nevznikne
```

```text
Open MZT + MTI
změna pořadí
Save
=> existující MTI má správně přegenerované RECORD=n
```

```text
projekt bez metadata
Save As NEW.MZT
NEW.MTI neexistuje
=> vytvoří se pouze NEW.MZT
```

```text
projekt bez metadata
Save As NEW.MZT
NEW.MTI před operací existuje
=> NEW.MTI se kompletně přegeneruje
```

```text
NEW.MZT před operací neexistuje
NEW.MTI existuje
Save As NEW.MZT
=> MTI se přesto aktualizuje
```

```text
OLD.MZT + OLD.MTI
Save As NEW.MZT
NEW.MTI neexistuje
=> OLD.MTI se nekopíruje
=> NEW.MTI nevznikne
=> další Save nesmí měnit OLD.MTI
```

```text
starý MTI má 7 RECORD
nový MZT má 4 records
=> výsledný MTI má přesně 4 RECORD
```

Totéž analogicky otestuj pro MZF/MFI.

---

# 36. Test přepnutí Basic/Advanced

Načti MZF s trailing a sidecarem.

V Basic:

- trailing není vidět,
- sidecar není vidět,
- pokročilé formáty nejsou ve filtrech.

Přepni Advanced bez reloadu:

- data projektu se nezmění,
- Advanced může trailing a profil zobrazit.

Přepni zpět do Basic:

- nic se nesmí smazat pouze přepnutím režimu.

Destruktivní změna nastává až při explicitním Save podle pravidel zvoleného režimu/formátu.

---

# 37. Zachování současných oprav

Před refaktorem porovnej současné chování a nenič:

- již opravenou grafiku,
- opravené parsování,
- existující validace,
- současné funkční QDF/MZQ chování,
- již opravené bugy,
- současná data, která reader/writer korektně zachovává.

Cílem není přepsat aplikaci od nuly.

Preferuj postupný refaktor s co nejmenším množstvím nesouvisejících změn.

---

# 38. Nepřidávej zbytečné závislosti

Použij stávající .NET/WPF stack.

Nevyměňuj UI framework.

Neměň target framework bez skutečné technické nutnosti.

Nepřidávej externí NuGet balíčky pro věci, které lze jednoduše řešit standardním .NET.

---

# 39. Po dokončení

Na konci práce vypiš:

1. které soubory byly změněny,
2. jak byl upraven datový model,
3. jak je oddělené MZF a MZT parsování,
4. jak je řešen RAW 104B description prostor,
5. jak funguje Basic trailing policy,
6. jak funguje Advanced trailing policy,
7. jak funguje MFI/MTI načítání,
8. přesný algoritmus rozhodnutí, zda při Save sidecar vytvořit/aktualizovat,
9. jak jsou řešené osiřelé existující sidecary v cíli,
10. jak se zachová profil při Move Up/Down,
11. jaké testy byly spuštěny a s jakým výsledkem,
12. zda zůstaly nějaké nevyřešené hraniční případy.

Neprováděj commit ani push bez dalšího explicitního pokynu.

---

# Hlavní principy, které nesmějí být porušeny

**Basic = původní jednoduchý QDTool + opravy, nikoliv nový odborný tape editor.**

**Advanced = místo pro nové a odborné funkce.**

**MZF RAW header data se nesmějí zničit textovou interpretací description oblasti.**

**Standalone MZF může mít trailing; MZT per-record trailing standardně nést nemůže.**

**Po Save musí viewer a interní aktuální dokument odpovídat tomu, co bylo skutečně uloženo.**

**Basic nikdy nevytváří nový sidecar tam, kde žádný není.**

**Existující cílový sidecar se ale vždy musí synchronizovat, aby nemohl zůstat nebezpečně zastaralý.**

**Profil patří k recordu, nikoliv k jeho pořadovému číslu.**

**Přepnutí Basic/Advanced samo o sobě nikdy nemění data.**
