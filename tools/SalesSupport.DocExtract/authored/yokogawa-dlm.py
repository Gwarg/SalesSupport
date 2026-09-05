"""In-session extraction of 'Brochure DLM5000, DLM5000HD, DLM3000, DLM3000HD.pdf' (D33, subscription path).
Sources: model/suffix tables p26-27, accessory table p28, model table p22, feature pages 2, 8, 16-19.
Usage: python dlm_extract.py <cache-file-name.json>
"""
import io, json, sys
from collections import Counter

hashname = sys.argv[1]
P = []


def A(**kv):
    return [{"key": k, "value": v} for k, v in kv.items()]


def rel(kind, targets, note=None):
    return [{"kind": kind, "target_model_code": t, "note": note} for t in targets]


SUFFIX = ("Power cord: -D UL/CSA and PSE, -F VDE/Korean, -Q British, -R Australian, -H Chinese, -N Brazilian, "
          "-T Taiwanese, -B Indian, -U IEC Plug Type B. Language (message and panel): -HJ Japanese, -HE English, "
          "-HC Chinese, -HG German, -HF French, -HK Korean, -HL Italian, -HS Spanish")
STD_ACC = ("Power cord, passive probes 701937 (one per channel unless /E2, /E3, /EX2 or /EX4), protective front cover, "
           "panel sheet, soft carrying case for probes, printer roll paper with /B5, start guide (manuals downloadable)")
BUS = "UART, I2C, SPI, CAN, CAN FD, LIN, FlexRay, SENT, CXPI and PSI5"
OSC = "Vågformsmätning > Oscilloskop"
ACC = "Vågformsmätning > Tillbehör och optioner"
SW = "Vågformsmätning > Programvara"

# ---- instruments ---------------------------------------------------------------
series = {}


def instrument(code, series_name, kind_name, ch, bw, res, logic, mem_opt, extra):
    desc = (f"The {code} is a {ch}-channel, {bw} {kind_name.lower()} in Yokogawa's {series_name} series, with {res} A/D "
            f"resolution and a maximum sample rate of 2.5 GS/s on all channels simultaneously. Standard memory holds "
            f"12.5 M points during continuous measurement and 50 M points in single mode (125 M points on odd channels "
            f"only){'; ' + mem_opt if mem_opt else ''}. The touchscreen operation panel, serial bus auto setup and "
            f"dedicated trigger/analysis options for {BUS} target automotive ECU and embedded development{extra}.")
    attrs = dict(series=series_name, bandwidth=bw, analog_channels=str(ch), ad_resolution=res, max_sample_rate="2.5 GS/s",
                 logic_input=logic,
                 standard_memory="12.5 M points continuous; 50 M points single mode (125 M points odd channels only)",
                 interfaces="USB 3.0, Ethernet 1000BASE-T/100BASE-TX/10BASE-T; GP-IB with /C1",
                 suffix_codes=SUFFIX, standard_accessories=STD_ACC)
    if mem_opt:
        attrs["memory_expansion"] = mem_opt
    P.append(dict(model_code=code, name=f"{kind_name}: {ch} ch, {bw}", kind="instrument", category_path=OSC,
                  description=desc, attributes=A(**attrs), aliases=[series_name, f"{series_name} {ch} ch"],
                  relations=[], status="active"))
    series.setdefault(series_name, []).append((code, ch))


HD_EXTRA = ("; the DLMsync connection synchronizes two units for multi-point measurement, and the 12-bit resolution "
            "suits inverter and motor switching evaluation with the power supply analysis option")
for code, ch, bw in [("DLM5038HD", 8, "350 MHz"), ("DLM5058HD", 8, "500 MHz"), ("DLM5034HD", 4, "350 MHz"), ("DLM5054HD", 4, "500 MHz")]:
    instrument(code, "DLM5000HD", "High Definition Oscilloscope", ch, bw, "12 bit", "16 bit standard, 32 bit with /L4",
               "memory expansion options raise this to 125 M points continuous and up to 1 G points single mode (/M3 for 8 ch, /M3S for 4 ch)",
               HD_EXTRA)
for code, ch, bw in [("DLM3034HD", 4, "350 MHz"), ("DLM3054HD", 4, "500 MHz")]:
    instrument(code, "DLM3000HD", "High Definition Oscilloscope", ch, bw, "12 bit", "8 bit switchable logic input (/LN removes it)",
               "memory expansion options raise this to 125 M points continuous and up to 1 G points single mode (/M3)", HD_EXTRA)
for code, ch, bw in [("DLM5038", 8, "350 MHz"), ("DLM5058", 8, "500 MHz"), ("DLM5034", 4, "350 MHz"), ("DLM5054", 4, "500 MHz")]:
    instrument(code, "DLM5000", "Mixed Signal Oscilloscope", ch, bw, "8 bit", "16 bit standard, 32 bit with /L32",
               "memory expansion options raise this to 50 M points continuous and 250 M/500 M points single mode (/M2 for 8 ch, /M2S for 4 ch)",
               "; the DLMsync connection synchronizes two units for up to 16 channels")
for code, ch, bw, kn in [("DLM3022", 2, "200 MHz", "Digital Oscilloscope"), ("DLM3024", 4, "200 MHz", "Mixed Signal Oscilloscope"),
                         ("DLM3032", 2, "350 MHz", "Digital Oscilloscope"), ("DLM3034", 4, "350 MHz", "Mixed Signal Oscilloscope"),
                         ("DLM3052", 2, "500 MHz", "Digital Oscilloscope"), ("DLM3054", 4, "500 MHz", "Mixed Signal Oscilloscope")]:
    instrument(code, "DLM3000", kn, ch, bw, "8 bit", "8 bit switchable logic input" if ch == 4 else "none",
               ("memory expansion options /M1 and /M2 (4 ch models) raise this to 50 M points continuous and 250 M/500 M points single mode"
                if ch == 4 else ""),
               " in a compact vertical-style chassis")


def hosts(series_name, chans=None):
    return [c for c, ch in series[series_name] if chans is None or ch in chans]


# ---- options (descriptions as printed in the model-and-suffix-code tables) -------
def option(series_name, code, desc, chans=None, group=None, note=None):
    hs = hosts(series_name, chans)
    attrs = dict(applies_to=f"{series_name} series" + (f" ({'/'.join(str(c) for c in chans)} ch models)" if chans else ""))
    if group:
        attrs["exclusive_group"] = group
    P.append(dict(model_code=code, name=desc, kind="option", category_path=ACC,
                  description=f"Orderable option {code} for the {series_name} series: {desc}." + (f" {note}" if note else ""),
                  attributes=A(**attrs), aliases=[], relations=rel("option_of", hs), status="active"))


MEM = "memory expansion - select only one"
G = "/G2, /G3, /GA - select only one"
SYNC = "Required on both main and sub unit together with a 701982 connection cable."
OPTIONS = {
    "DLM5000HD": [
        ("/L4", "Expansion logic 16 bit (Total 32 bit)"), ("/B5", "Built-in printer (112 mm)"),
        ("/M1", "Memory expansion option (8 ch model only): 25 M points continuous; 125 M/250 M points single mode", (8,), MEM),
        ("/M2", "Memory expansion option (8 ch model only): 50 M points continuous; 250 M/500 M points single mode", (8,), MEM),
        ("/M3", "Memory expansion option (8 ch model only): 125 M points continuous; 500 M points/1 G points single mode", (8,), MEM),
        ("/M1S", "Memory expansion option (4 ch model only): 25 M points continuous; 125 M/250 M points single mode", (4,), MEM),
        ("/M2S", "Memory expansion option (4 ch model only): 50 M points continuous; 250 M/500 M points single mode", (4,), MEM),
        ("/M3S", "Memory expansion option (4 ch model only): 125 M points continuous; 500 M points/1 G points single mode", (4,), MEM),
        ("/P8", "8 probe power terminals (for 8 ch model)", (8,)), ("/P4", "4 probe power terminals (for 4 ch model)", (4,)),
        ("/C1", "GP-IB interface"), ("/C8", "Internal storage (64 GB)"), ("/CY", "IEEE1588 master function"),
        ("/SY", "Synchronous Operation", None, None, SYNC),
        ("/G2", "User-defined math function", None, G), ("/G3", "Power supply analysis function", None, G),
        ("/GA", "User-defined math function + Power supply analysis function", None, G),
        ("/F1", "UART + I2C + SPI trigger and analysis"), ("/F2", "CAN + CAN FD + LIN trigger and analysis"),
        ("/F3", "FlexRay trigger and analysis"), ("/F4", "SENT trigger and analysis"),
        ("/F5", "CXPI trigger and analysis"), ("/F6", "PSI5 trigger and analysis"),
        ("/E1", "Four additional 701937 probes (8 in total) (for 8 ch model)", (8,)),
        ("/E2", "Attach four 701949 probes"), ("/E3", "Attach eight 701949 probes (for 8 ch model)", (8,)),
    ],
    "DLM3000HD": [
        ("/LN", "No switchable logic input"), ("/B5", "Built-in printer (112 mm)"),
        ("/M1", "Memory expansion option: 25 Mpoints continuous; 125 Mpoints/250 Mpoints single mode", None, MEM),
        ("/M2", "Memory expansion option: 50 Mpoints continuous; 250 Mpoints/500 Mpoints single mode", None, MEM),
        ("/M3", "Memory expansion option: 125 Mpoints continuous; 500 Mpoints/1 G points single mode", None, MEM),
        ("/P4", "4 probe power terminals"),
        ("/C1", "GP-IB interface + GO/NO-GO terminal", None, "/C1 and /SY - select only one"),
        ("/SY", "Synchronous Operation", None, "/C1 and /SY - select only one", SYNC),
        ("/C8", "Internal storage (60 GB)"), ("/CY", "IEEE1588 master function"),
        ("/G2", "User-defined math function", None, G), ("/G3", "Power supply analysis function", None, G),
        ("/GA", "User-defined math function + Power supply analysis function", None, G),
        ("/F1", "UART + I2C + SPI trigger and analysis"), ("/F2", "CAN + CAN FD + LIN trigger and analysis"),
        ("/F3", "FlexRay trigger and analysis"), ("/F4", "SENT trigger and analysis"),
        ("/F5", "CXPI trigger and analysis"), ("/F6", "PSI5 trigger and analysis"),
        ("/EX4", "Replace all probes with 701949"),
    ],
    "DLM5000": [
        ("/L32", "Expansion logic 16 bit (Total 32 bit)"), ("/B5", "Built-in printer (112 mm)"),
        ("/M1", "Memory expansion option (8 ch model only): 25 M points continuous; 125 M/250 M points single mode", (8,), MEM),
        ("/M2", "Memory expansion option (8 ch model only): 50 M points continuous; 250 M/500 M points single mode", (8,), MEM),
        ("/M1S", "Memory expansion option (4 ch model only): 25 M points continuous; 125 M/250 M points single mode", (4,), MEM),
        ("/M2S", "Memory expansion option (4 ch model only): 50 M points continuous; 250 M/500 M points single mode", (4,), MEM),
        ("/P8", "8 probe power terminals (for 8 ch model)", (8,)), ("/P4", "4 probe power terminals (for 4 ch model)", (4,)),
        ("/C1", "GP-IB interface"), ("/C8", "Internal storage (64 GB)"),
        ("/SYN", "Synchronous Operation", None, None, SYNC),
        ("/G02", "User-defined math function"), ("/G03", "Power supply analysis function"),
        ("/F01", "UART + I2C + SPI trigger and analysis"), ("/F02", "CAN + CAN FD + LIN trigger and analysis"),
        ("/F03", "FlexRay trigger and analysis"), ("/F04", "SENT trigger and analysis"),
        ("/F05", "CXPI trigger and analysis"), ("/F06", "PSI5 trigger and analysis"),
        ("/E1", "Four additional 701937 probes (8 in total) (for 8 ch model)", (8,)),
        ("/E2", "Attach four 701949 probes"), ("/E3", "Attach eight 701949 probes (for 8 ch model)", (8,)),
    ],
    "DLM3000": [
        ("/LN", "No switchable logic input (4 ch model only)", (4,)), ("/B5", "Built-in printer (112 mm)"),
        ("/M1", "Memory expansion option (4 ch model only): 25 Mpoints continuous; 125 Mpoints/250 Mpoints single mode", (4,), MEM),
        ("/M2", "Memory expansion option (4 ch model only): 50 Mpoints continuous; 250 Mpoints/500 Mpoints single mode", (4,), MEM),
        ("/P2", "2 probe power terminals (for 2 ch model)", (2,)), ("/P4", "4 probe power terminals (for 4 ch model)", (4,)),
        ("/C1", "GP-IB interface + GO/NO-GO terminal"), ("/C8", "Internal storage (60 GB)"),
        ("/G02", "User-defined math function (4 ch model only)", (4,)), ("/G03", "Power supply analysis function (4 ch model only)", (4,)),
        ("/F01", "UART + I2C + SPI trigger and analysis (4 ch model only)", (4,)),
        ("/F02", "CAN + CAN FD + LIN trigger and analysis (4 ch model only)", (4,)),
        ("/F03", "FlexRay trigger and analysis (4 ch model only)", (4,)), ("/F04", "SENT trigger and analysis (4 ch model only)", (4,)),
        ("/F05", "CXPI trigger and analysis (4 ch model only)", (4,)), ("/F06", "PSI5 trigger and analysis (4 ch model only)", (4,)),
        ("/EX2", "Replace all probes with 701949 (2 ch model only)", (2,)), ("/EX4", "Replace all probes with 701949 (4 ch model only)", (4,)),
    ],
}
for s, entries in OPTIONS.items():
    for entry in entries:
        code, desc = entry[0], entry[1]
        chans = entry[2] if len(entry) > 2 else None
        group = entry[3] if len(entry) > 3 else None
        note = entry[4] if len(entry) > 4 else None
        option(s, code, desc, chans, group, note)

# ---- additional option licenses (post-purchase) ----------------------------------
for code, s, suffixes, chans in [
    ("709823", "DLM5000HD", "-CY, -SY, -G2, -G3, -F1, -F2, -F3, -F4, -F5, -F6", None),
    ("709813", "DLM3000HD", "-CY, -G2, -G3, -F1, -F2, -F3, -F4, -F5, -F6", None),
    ("709821", "DLM5000", "-G02, -G03, -F01, -F02, -F03, -F04, -F05, -F06, -SYN", None),
    ("709811", "DLM3000", "-G02, -G03, -F01, -F02, -F03, -F04, -F05, -F06", (4,)),
]:
    P.append(dict(model_code=code, name=f"Additional Option License for {s}", kind="option", category_path=ACC,
                  description=(f"Post-purchase option license {code} that adds analysis functions to an existing {s} series "
                               f"oscilloscope; the function is selected by suffix code ({suffixes})."),
                  attributes=A(applies_to=f"{s} series" + (" (4 ch models only)" if chans else ""), suffix_codes=suffixes),
                  aliases=[], relations=rel("option_of", hosts(s, chans)), status="active"))

# ---- accessories (Accessory Models table, p28) -------------------------------------
ALL = [c for s in series.values() for c, _ in s]
S5 = hosts("DLM5000HD") + hosts("DLM5000")
S3 = hosts("DLM3000HD") + hosts("DLM3000")


def acc(code, name, spec, targets=ALL, aliases=(), **attrs):
    P.append(dict(model_code=code, name=name, kind="accessory", category_path=ACC,
                  description=f"{name} {code} for the DLM series oscilloscopes: {spec}.",
                  attributes=A(specification=spec, **attrs), aliases=list(aliases),
                  relations=rel("accessory_of", targets), status="active"))


acc("701937", "Passive probe", "10 MΩ (10:1), 500 MHz, 1.3 m", bandwidth="500 MHz", attenuation="10:1", input_impedance="10 MΩ", cable_length="1.3 m")
acc("701949", "Miniature passive probe", "10 MΩ (10:1), 500 MHz, 1.3 m", bandwidth="500 MHz", attenuation="10:1", cable_length="1.3 m")
acc("702907", "Passive probe (Wide temperature range)", "10 MΩ (10:1), 200 MHz, 2.5 m, -40 to +85 degrees C", bandwidth="200 MHz", operating_temperature="-40 to +85 degrees C", cable_length="2.5 m")
acc("700939", "FET probe", "DC to 900 MHz BW, 2.5 MΩ/1.8 pF", bandwidth="DC to 900 MHz", input_impedance="2.5 MΩ / 1.8 pF")
acc("701944", "100:1 voltage probe", "DC to 400 MHz BW, 1.2 m, 1000 Vrms", bandwidth="DC to 400 MHz", max_input="1000 Vrms", cable_length="1.2 m")
acc("701945", "100:1 voltage probe", "DC to 250 MHz BW, 3 m, 1000 Vrms", bandwidth="DC to 250 MHz", max_input="1000 Vrms", cable_length="3 m")
acc("701977", "Differential probe", "DC to 50 MHz BW, max. ±7000 V", bandwidth="DC to 50 MHz", max_input="±7000 V")
acc("701978", "Differential probe", "DC to 150 MHz BW, max. ±1500 V", bandwidth="DC to 150 MHz", max_input="±1500 V")
acc("701924", "Differential probe (PBDH1000)", "DC to 1 GHz BW, 1 MΩ, max. ±25 V", aliases=("PBDH1000",), bandwidth="DC to 1 GHz", max_input="±25 V")
acc("701925", "Differential probe (PBDH0500)", "DC to 500 MHz BW, max. ±25 V, input impedance 1 MΩ approx. 1.1 pF", aliases=("PBDH0500",), bandwidth="DC to 500 MHz", max_input="±25 V")
acc("702921", "Differential probe (PBDH0400)", "DC to 400 MHz BW, max. ±1000 V", aliases=("PBDH0400",), bandwidth="DC to 400 MHz", max_input="±1000 V")
acc("702922", "Differential probe (PBDH0400)", "DC to 400 MHz BW, max. ±2000 V", aliases=("PBDH0400",), bandwidth="DC to 400 MHz", max_input="±2000 V")
acc("701927", "Differential probe (PBDH0150)", "DC to 150 MHz BW, max. ±1400 V", aliases=("PBDH0150",), bandwidth="DC to 150 MHz", max_input="±1400 V")
acc("701917", "Current probe", "DC to 50 MHz BW, 5 Arms", bandwidth="DC to 50 MHz", max_current="5 Arms")
acc("701918", "Current probe", "DC to 120 MHz BW, 5 Arms", bandwidth="DC to 120 MHz", max_current="5 Arms")
acc("701929", "Current probe (PBC050)", "DC to 50 MHz BW, 30 Arms", aliases=("PBC050",), bandwidth="DC to 50 MHz", max_current="30 Arms")
acc("701928", "Current probe (PBC100)", "DC to 100 MHz BW, 30 Arms", aliases=("PBC100",), bandwidth="DC to 100 MHz", max_current="30 Arms")
acc("701930", "Current probe", "DC to 10 MHz BW, 150 Arms", bandwidth="DC to 10 MHz", max_current="150 Arms")
acc("701931", "Current probe", "DC to 2 MHz BW, 500 Arms", bandwidth="DC to 2 MHz", max_current="500 Arms")
acc("702915", "Current probe", "DC to 50 MHz BW, 0.5, 5, 30 Arms", bandwidth="DC to 50 MHz", max_current="0.5 / 5 / 30 Arms")
acc("702916", "Current probe", "DC to 120 MHz BW, 0.5, 5, 30 Arms", bandwidth="DC to 120 MHz", max_current="0.5 / 5 / 30 Arms")
acc("701988", "Logic probe (PBL100)", "1 MΩ, toggle frequency of 100 MHz - sold separately for the logic input", aliases=("PBL100",), input_impedance="1 MΩ, 10 pF", toggle_frequency="100 MHz")
acc("701989", "Logic probe (PBL250)", "100 kΩ, toggle frequency of 250 MHz - sold separately for the logic input", aliases=("PBL250",), input_impedance="100 kΩ, 3 pF", toggle_frequency="250 MHz")
acc("701936", "Deskew correction signal source", "For deskew correction")
acc("366973", "Go/No-Go Cable", "For GO/NO-GO output terminal")
acc("B9988AE", "Printer roll paper", "Lot size is 10 rolls, 10 meters each - for the /B5 built-in printer")
acc("701919", "Probe stand", "Round base, 1 arm")
acc("701968", "Soft carrying case", "For DLM5000HD/DLM5000 with 3 pockets for storage", targets=S5)
acc("701964", "Soft carrying case", "For DLM3000HD/DLM3000 with 3 pockets for storage", targets=S3)
acc("701969-E", "Rack mount kit", "For DLM5000HD/DLM5000 (EIA standard compliant)", targets=S5)
acc("701969-J", "Rack mount kit", "For DLM5000HD/DLM5000 (JIS standard compliant)", targets=S5)
acc("701982-01", "Connection cable", "Connection cable for DLM synchronous operation (DLMsync), 1.0 m", cable_length="1.0 m")
acc("701982-02", "Connection cable", "Connection cable for DLM synchronous operation (DLMsync), 2.8 m", cable_length="2.8 m")
acc("701934", "Probe power supply", "A power supply for current probes, FET probes and differential probes; provides power for up to four probes, including large current probes")

# ---- software ----------------------------------------------------------------------
SOFTWARE = [
    ("IS8001", "IS8000 Integrated Software Platform (subscription)",
     "Subscription (annual license) for the IS8000 integrated software platform that remotely controls, monitors and configures "
     "Yokogawa power analyzers, recorders and oscilloscopes and synchronizes measurements with ECU monitors, high-speed cameras "
     "and Modbus/TCP devices", "subscription (annual)", ["IS8000"]),
    ("IS8002", "IS8000 Integrated Software Platform (perpetual)",
     "Perpetual (permanent license) for the IS8000 integrated software platform", "perpetual", ["IS8000"]),
    ("IS8002CDV", "Classic Data Viewer",
     "Perpetual (permanent license) PC software with Xviewer functions: remote control of the instrument, waveform observation "
     "and analysis, cursor and parametric measurements, statistics, multiple-file display, reporting and file transfer; free of "
     "charge for the duration of a purchased IS8001/IS8002 license", "perpetual", ["Xviewer", "Classic Data Viewer"]),
]
for code, name, spec, lic, aliases in SOFTWARE:
    P.append(dict(model_code=code, name=name, kind="software", category_path=SW, description=f"{spec}.",
                  attributes=A(license=lic), aliases=aliases, relations=rel("software_for", ALL), status="active"))

notes = [
    "Authored in-session from the brochure text (no API extraction): model and suffix code tables pages 26-27, accessory models page 28, model table page 22, feature pages 2, 8, 16-19.",
    "Series names DLM5000HD, DLM3000HD, DLM5000 and DLM3000 are not orderable model codes; they are recorded as aliases on each member model.",
    "Free software without model codes (IS8000 Simple, XWirepuller, TMCTL control library, DL-Term, LabVIEW drivers, MATLAB WDF Access ToolBox) was not listed as products.",
    "Option code naming differs by series: /F1-/F6, /G2, /G3, /SY on the HD models versus /F01-/F06, /G02, /G03, /SYN on DLM5000/DLM3000.",
    "Memory options /M1-/M3 and /M1S-/M3S are mutually exclusive within a series; /G2, /G3 and /GA likewise; on DLM3000HD /C1 and /SY are mutually exclusive.",
]
out = f"testdata/.extract-cache/{hashname}"
io.open(out, "w", encoding="utf-8").write(json.dumps({"products": P, "notes": notes}, ensure_ascii=False, indent=1))
print(f"wrote {out}: {len(P)} products", dict(Counter(p["kind"] for p in P)))
