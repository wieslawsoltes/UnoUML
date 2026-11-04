using System.Globalization;
using UnoUML.PlantUml;

namespace UnoUML.PlantUml.Tests;

internal static class BuiltinCases
{
    // Expected expansions were checked against PlantUML 1.2026.9 with UTC/en locale.
    private static readonly (string Name, string Expression, string Expected)[] Cases =
    [
        ("boolean",
            """%true() / %false() / %boolval("TRUE") / %boolval(0) / %not(1) / %and(1,2) / %or(0,2) / %xor(1,2) / %nand(1,2) / %nor(0,0) / %nxor(1,2)""",
            """1 / 0 / 1 / 0 / 0 / 1 / 1 / 0 / 0 / 1 / 1"""),
        ("string",
            """%strlen("𝄞hello") / %string(42) / %upper("straße ﬃ ΐ") / %lower("ΟΣ İ HELLO")""",
            """7 / 42 / STRASSE FFI Ϊ́ / ος i̇ hello"""),
        ("substring",
            """%substr("abcdef",2) / %substr("abcdef",2,2) / %substr("abcdef",9) / %strpos("hello","ll") / %strpos("hello","bad")""",
            """cdef / cd /  / 2 / -1"""),
        ("split",
            """%splitstr("abc:def;ghi::",":;") / %splitstr("", ",") / %splitstr("abc", "") / %splitstr("a😀b😀c", "😀")""",
            """["abc","def","ghi"] / [] / ["abc"] / ["a","b","c"]"""),
        ("regex",
            """%splitstr_regex("AbcDefGhi","(?=[A-Z])") / %splitstr_regex("a:b:","(:)") / %splitstr_regex("",":") / %splitstr_regex("ab","")""",
            """["Abc","Def","Ghi"] / ["a","b"] / [""] / ["a","b"]"""),
        ("number",
            """%intval("+42") / %hex2dec("-ff") / %hex2dec("ffffffff") / %dec2hex(-1) / %mod(-10,3) / %mod(-2147483648,-1)""",
            """42 / -255 / 0 / ffffffff / -1 / 0"""),
        ("unicode",
            """%chr(128512) / %ord("😀abc") / %ord("")""",
            """😀 / 128512 / 0"""),
        ("sizes",
            """%size(42) / %size("42") / %size($object) / %size($array) / %size($object.flag)""",
            """0 / 2 / 5 / 3 / 0"""),
        ("variables",
            """%variable_exists("$object") / %variable_exists("missing") / %get_variable_value("$array") / %function_exists("%upper") / %function_exists("missing")""",
            """1 / 0 / ["a","b",3] / 1 / 0"""),
        ("json_types",
            """%get_json_type("hello") / %get_json_type(42) / %get_json_type($object) / %get_json_type($array) / %get_json_type($object.flag) / %get_json_type($object.nil)""",
            """string / number / object / array / boolean / json"""),
        ("json_keys",
            """%get_json_keys($object) / %get_json_keys($keys) / %json_key_exists($object,"a") / %json_key_exists($object,"z") / %json_key_exists("abc","x")""",
            """["a","nested","list","flag","nil"] / ["a","b","a"] / 1 / 0 / 0"""),
        ("json_add",
            """%json_add($array,"tail") / %json_add($object,"new",4) / %json_add($object,"a",9)""",
            """["a","b",3,"tail"] / {"a":1,"nested":{"x":2},"list":[1,2],"flag":true,"nil":null,"new":4} / {"a":1,"nested":{"x":2},"list":[1,2],"flag":true,"nil":null,"a":9}"""),
        ("json_remove",
            """%json_remove($array,1) / %json_remove($array,9) / %json_remove($object,"a")""",
            """["a",3] / ["a","b",3] / {"nested":{"x":2},"list":[1,2],"flag":true,"nil":null}"""),
        ("json_set",
            """%json_set($array,1,42) / %json_set($object,"a",7) / %json_set($object,$other)""",
            """["a",42,3] / {"a":7,"nested":{"x":2},"list":[1,2],"flag":true,"nil":null} / {"a":5,"nested":{"x":2,"y":3},"list":[1,2],"flag":true,"nil":null,"b":7}"""),
        ("json_merge",
            """%json_merge($object,$other) / %json_merge($array,$array) / %json_merge($array,$object)""",
            """{"a":5,"nested":{"y":3},"list":[1,2],"flag":true,"nil":null,"b":7} / ["a","b",3,"a","b",3] / ["a","b",3]"""),
        ("json_parse",
            """%str2json("bad json") / %str2json("null") / %str2json("false") / %str2json("42")""",
            """ / null / false / 42"""),
        ("load_default",
            """%load_json("missing.json") / %load_json("missing.json","[1,2]")""",
            """{} / [1,2]"""),
        ("features",
            """%feature("theme") / %feature("style") / %feature("unknown") / %version() / %eval("2+3*4") / %eval("hello")""",
            """1 / 1 / 0 / 1.2026.9 / 14 / 0"""),
        ("current_theme",
            """%get_current_theme()""",
            """{}"""),
        ("date",
            """%date("yyyy-MM-dd HH:mm:ss",1700000000,"UTC") / %date("EEE D w W F u a h k K G",1700000000,"UTC")""",
            """2023-11-14 22:13:20 / Tue 318 46 3 2 2 PM 10 22 10 AD"""),
        ("date_weekyear",
            """%date("YYYY-ww yyyy-MM-dd XXX",1640995200,"UTC")""",
            """2022-01 2022-01-01 Z"""),
        ("date_zone",
            """%date("yyyy-MM-dd HH:mm:ss z Z XXX",1700000000,"Europe/Warsaw")""",
            """2023-11-14 23:13:20 CET +0100 +01:00"""),
        ("date_quotes",
            """%date("yyyy.MM.dd' at 'HH:mm 'o''clock'",1700000000,"UTC")""",
            """2023.11.14 at 22:13 o'clock"""),
        ("color",
            """%lighten("red",20) / %darken("red",20) / %reverse_color("#ff7700") / %hsl_color(120,100,50) / %hsl_color(120,100,50,50)""",
            """#FF3333 / #CC0000 / #0088FF / #00FF00 / #8000ff00"""),
        ("color_luminance",
            """%is_dark("red") / %is_light("red") / %is_dark("#808080") / %is_dark("#7f7f7f")""",
            """1 / 0 / 0 / 1"""),
        ("color_bounds",
            """%hsl_color(-60,120,50) / %lighten("#123456",300) / %darken("#abcdef",200) / %hsl_color(120,100,50,0)""",
            """#FF00FF / #B1D0EF / #000000 / transparent"""),
        ("hsluv",
            """%reverse_hsluv_color("#ff7700") / %reverse_hsluv_color("black") / %reverse_hsluv_color("white") / %reverse_hsluv_color("#123456")""",
            """#602800 / #767676 / #767676 / #5FA2F1"""),
        ("symbols",
            """%dollar() / %percent()""",
            """$ / %"""),
        ("random",
            """%random(1) / %random(0,1)""",
            """0 / 0"""),
    ];


    // Frozen Java SimpleDateFormat results cover legacy timezone aliases, DST,
    // locale calendars/digits/month forms, ISO week boundaries and invalid input.
    private static readonly (string Name, string Source, string? Expected, string Culture, string Zone, long Instant)[] DateCases =
    [
        ("zone_ACT_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"ACT\")", "A -> B: 2023-11-15 07:43:20 ACST Australian Central Standard Time +0930 +09 +0930 +09:30", "en-US", "UTC", 1700000000000L),
        ("zone_ACT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"ACT\")", "A -> B: 2023-03-28 20:10:00 ACST Australian Central Standard Time +0930 +09 +0930 +09:30", "en-US", "UTC", 1680000000000L),
        ("zone_AET_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"AET\")", "A -> B: 2023-11-15 09:13:20 AEDT Australian Eastern Daylight Time +1100 +11 +1100 +11:00", "en-US", "UTC", 1700000000000L),
        ("zone_AET_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"AET\")", "A -> B: 2023-03-28 21:40:00 AEDT Australian Eastern Daylight Time +1100 +11 +1100 +11:00", "en-US", "UTC", 1680000000000L),
        ("zone_AGT_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"AGT\")", "A -> B: 2023-11-14 19:13:20 ART Argentine Time -0300 -03 -0300 -03:00", "en-US", "UTC", 1700000000000L),
        ("zone_AGT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"AGT\")", "A -> B: 2023-03-28 07:40:00 ART Argentine Time -0300 -03 -0300 -03:00", "en-US", "UTC", 1680000000000L),
        ("zone_ART_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"ART\")", "A -> B: 2023-11-15 00:13:20 EET Eastern European Standard Time +0200 +02 +0200 +02:00", "en-US", "UTC", 1700000000000L),
        ("zone_ART_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"ART\")", "A -> B: 2023-03-28 12:40:00 EET Eastern European Standard Time +0200 +02 +0200 +02:00", "en-US", "UTC", 1680000000000L),
        ("zone_AST_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"AST\")", "A -> B: 2023-11-14 13:13:20 AKST Alaska Standard Time -0900 -09 -0900 -09:00", "en-US", "UTC", 1700000000000L),
        ("zone_AST_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"AST\")", "A -> B: 2023-03-28 02:40:00 AKDT Alaska Daylight Time -0800 -08 -0800 -08:00", "en-US", "UTC", 1680000000000L),
        ("zone_BET_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"BET\")", "A -> B: 2023-11-14 19:13:20 BRT Brasilia Standard Time -0300 -03 -0300 -03:00", "en-US", "UTC", 1700000000000L),
        ("zone_BET_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"BET\")", "A -> B: 2023-03-28 07:40:00 BRT Brasilia Standard Time -0300 -03 -0300 -03:00", "en-US", "UTC", 1680000000000L),
        ("zone_BST_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"BST\")", "A -> B: 2023-11-15 04:13:20 BDT Bangladesh Standard Time +0600 +06 +0600 +06:00", "en-US", "UTC", 1700000000000L),
        ("zone_BST_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"BST\")", "A -> B: 2023-03-28 16:40:00 BDT Bangladesh Standard Time +0600 +06 +0600 +06:00", "en-US", "UTC", 1680000000000L),
        ("zone_CAT_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"CAT\")", "A -> B: 2023-11-15 00:13:20 CAT Central Africa Time +0200 +02 +0200 +02:00", "en-US", "UTC", 1700000000000L),
        ("zone_CAT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"CAT\")", "A -> B: 2023-03-28 12:40:00 CAT Central Africa Time +0200 +02 +0200 +02:00", "en-US", "UTC", 1680000000000L),
        ("zone_CNT_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"CNT\")", "A -> B: 2023-11-14 18:43:20 NST Newfoundland Standard Time -0330 -03 -0330 -03:30", "en-US", "UTC", 1700000000000L),
        ("zone_CNT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"CNT\")", "A -> B: 2023-03-28 08:10:00 NDT Newfoundland Daylight Time -0230 -02 -0230 -02:30", "en-US", "UTC", 1680000000000L),
        ("zone_CST_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"CST\")", "A -> B: 2023-11-14 16:13:20 CST Central Standard Time -0600 -06 -0600 -06:00", "en-US", "UTC", 1700000000000L),
        ("zone_CST_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"CST\")", "A -> B: 2023-03-28 05:40:00 CDT Central Daylight Time -0500 -05 -0500 -05:00", "en-US", "UTC", 1680000000000L),
        ("zone_CTT_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"CTT\")", "A -> B: 2023-11-15 06:13:20 CST China Standard Time +0800 +08 +0800 +08:00", "en-US", "UTC", 1700000000000L),
        ("zone_CTT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"CTT\")", "A -> B: 2023-03-28 18:40:00 CST China Standard Time +0800 +08 +0800 +08:00", "en-US", "UTC", 1680000000000L),
        ("zone_EAT_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"EAT\")", "A -> B: 2023-11-15 01:13:20 EAT East Africa Time +0300 +03 +0300 +03:00", "en-US", "UTC", 1700000000000L),
        ("zone_EAT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"EAT\")", "A -> B: 2023-03-28 13:40:00 EAT East Africa Time +0300 +03 +0300 +03:00", "en-US", "UTC", 1680000000000L),
        ("zone_ECT_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"ECT\")", "A -> B: 2023-11-14 23:13:20 CET Central European Standard Time +0100 +01 +0100 +01:00", "en-US", "UTC", 1700000000000L),
        ("zone_ECT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"ECT\")", "A -> B: 2023-03-28 12:40:00 CEST Central European Summer Time +0200 +02 +0200 +02:00", "en-US", "UTC", 1680000000000L),
        ("zone_IET_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"IET\")", "A -> B: 2023-11-14 17:13:20 EST Eastern Standard Time -0500 -05 -0500 -05:00", "en-US", "UTC", 1700000000000L),
        ("zone_IET_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"IET\")", "A -> B: 2023-03-28 06:40:00 EDT Eastern Daylight Time -0400 -04 -0400 -04:00", "en-US", "UTC", 1680000000000L),
        ("zone_IST_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"IST\")", "A -> B: 2023-11-15 03:43:20 IST India Standard Time +0530 +05 +0530 +05:30", "en-US", "UTC", 1700000000000L),
        ("zone_IST_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"IST\")", "A -> B: 2023-03-28 16:10:00 IST India Standard Time +0530 +05 +0530 +05:30", "en-US", "UTC", 1680000000000L),
        ("zone_JST_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"JST\")", "A -> B: 2023-11-15 07:13:20 JST Japan Standard Time +0900 +09 +0900 +09:00", "en-US", "UTC", 1700000000000L),
        ("zone_JST_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"JST\")", "A -> B: 2023-03-28 19:40:00 JST Japan Standard Time +0900 +09 +0900 +09:00", "en-US", "UTC", 1680000000000L),
        ("zone_MIT_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"MIT\")", "A -> B: 2023-11-15 11:13:20 WSST Apia Standard Time +1300 +13 +1300 +13:00", "en-US", "UTC", 1700000000000L),
        ("zone_MIT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"MIT\")", "A -> B: 2023-03-28 23:40:00 WSST Apia Standard Time +1300 +13 +1300 +13:00", "en-US", "UTC", 1680000000000L),
        ("zone_NET_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"NET\")", "A -> B: 2023-11-15 02:13:20 AMT Armenia Standard Time +0400 +04 +0400 +04:00", "en-US", "UTC", 1700000000000L),
        ("zone_NET_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"NET\")", "A -> B: 2023-03-28 14:40:00 AMT Armenia Standard Time +0400 +04 +0400 +04:00", "en-US", "UTC", 1680000000000L),
        ("zone_NST_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"NST\")", "A -> B: 2023-11-15 11:13:20 NZDT New Zealand Daylight Time +1300 +13 +1300 +13:00", "en-US", "UTC", 1700000000000L),
        ("zone_NST_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"NST\")", "A -> B: 2023-03-28 23:40:00 NZDT New Zealand Daylight Time +1300 +13 +1300 +13:00", "en-US", "UTC", 1680000000000L),
        ("zone_PLT_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"PLT\")", "A -> B: 2023-11-15 03:13:20 PKT Pakistan Standard Time +0500 +05 +0500 +05:00", "en-US", "UTC", 1700000000000L),
        ("zone_PLT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"PLT\")", "A -> B: 2023-03-28 15:40:00 PKT Pakistan Standard Time +0500 +05 +0500 +05:00", "en-US", "UTC", 1680000000000L),
        ("zone_PNT_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"PNT\")", "A -> B: 2023-11-14 15:13:20 MST Mountain Standard Time -0700 -07 -0700 -07:00", "en-US", "UTC", 1700000000000L),
        ("zone_PNT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"PNT\")", "A -> B: 2023-03-28 03:40:00 MST Mountain Standard Time -0700 -07 -0700 -07:00", "en-US", "UTC", 1680000000000L),
        ("zone_PRT_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"PRT\")", "A -> B: 2023-11-14 18:13:20 AST Atlantic Standard Time -0400 -04 -0400 -04:00", "en-US", "UTC", 1700000000000L),
        ("zone_PRT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"PRT\")", "A -> B: 2023-03-28 06:40:00 AST Atlantic Standard Time -0400 -04 -0400 -04:00", "en-US", "UTC", 1680000000000L),
        ("zone_PST_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"PST\")", "A -> B: 2023-11-14 14:13:20 PST Pacific Standard Time -0800 -08 -0800 -08:00", "en-US", "UTC", 1700000000000L),
        ("zone_PST_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"PST\")", "A -> B: 2023-03-28 03:40:00 PDT Pacific Daylight Time -0700 -07 -0700 -07:00", "en-US", "UTC", 1680000000000L),
        ("zone_SST_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"SST\")", "A -> B: 2023-11-15 09:13:20 SBT Solomon Islands Time +1100 +11 +1100 +11:00", "en-US", "UTC", 1700000000000L),
        ("zone_SST_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"SST\")", "A -> B: 2023-03-28 21:40:00 SBT Solomon Islands Time +1100 +11 +1100 +11:00", "en-US", "UTC", 1680000000000L),
        ("zone_VST_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"VST\")", "A -> B: 2023-11-15 05:13:20 ICT Indochina Time +0700 +07 +0700 +07:00", "en-US", "UTC", 1700000000000L),
        ("zone_VST_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"VST\")", "A -> B: 2023-03-28 17:40:00 ICT Indochina Time +0700 +07 +0700 +07:00", "en-US", "UTC", 1680000000000L),
        ("zone_EST_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"EST\")", "A -> B: 2023-11-14 17:13:20 EST Eastern Standard Time -0500 -05 -0500 -05:00", "en-US", "UTC", 1700000000000L),
        ("zone_EST_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"EST\")", "A -> B: 2023-03-28 05:40:00 EST Eastern Standard Time -0500 -05 -0500 -05:00", "en-US", "UTC", 1680000000000L),
        ("zone_MST_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"MST\")", "A -> B: 2023-11-14 15:13:20 MST Mountain Standard Time -0700 -07 -0700 -07:00", "en-US", "UTC", 1700000000000L),
        ("zone_MST_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"MST\")", "A -> B: 2023-03-28 03:40:00 MST Mountain Standard Time -0700 -07 -0700 -07:00", "en-US", "UTC", 1680000000000L),
        ("zone_HST_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"HST\")", "A -> B: 2023-11-14 12:13:20 HST Hawaii Standard Time -1000 -10 -1000 -10:00", "en-US", "UTC", 1700000000000L),
        ("zone_HST_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"HST\")", "A -> B: 2023-03-28 00:40:00 HST Hawaii Standard Time -1000 -10 -1000 -10:00", "en-US", "UTC", 1680000000000L),
        ("zone_UTC_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"UTC\")", "A -> B: 2023-11-14 22:13:20 UTC Coordinated Universal Time +0000 Z Z Z", "en-US", "UTC", 1700000000000L),
        ("zone_UTC_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"UTC\")", "A -> B: 2023-03-28 10:40:00 UTC Coordinated Universal Time +0000 Z Z Z", "en-US", "UTC", 1680000000000L),
        ("zone_Etc/UTC_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"Etc/UTC\")", "A -> B: 2023-11-14 22:13:20 UTC Coordinated Universal Time +0000 Z Z Z", "en-US", "UTC", 1700000000000L),
        ("zone_Etc/UTC_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"Etc/UTC\")", "A -> B: 2023-03-28 10:40:00 UTC Coordinated Universal Time +0000 Z Z Z", "en-US", "UTC", 1680000000000L),
        ("zone_GMT_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"GMT\")", "A -> B: 2023-11-14 22:13:20 GMT Greenwich Mean Time +0000 Z Z Z", "en-US", "UTC", 1700000000000L),
        ("zone_GMT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"GMT\")", "A -> B: 2023-03-28 10:40:00 GMT Greenwich Mean Time +0000 Z Z Z", "en-US", "UTC", 1680000000000L),
        ("zone_Europe/London_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"Europe/London\")", "A -> B: 2023-11-14 22:13:20 GMT Greenwich Mean Time +0000 Z Z Z", "en-US", "UTC", 1700000000000L),
        ("zone_Europe/London_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"Europe/London\")", "A -> B: 2023-03-28 11:40:00 BST British Summer Time +0100 +01 +0100 +01:00", "en-US", "UTC", 1680000000000L),
        ("zone_Europe/Warsaw_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"Europe/Warsaw\")", "A -> B: 2023-11-14 23:13:20 CET Central European Standard Time +0100 +01 +0100 +01:00", "en-US", "UTC", 1700000000000L),
        ("zone_Europe/Warsaw_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"Europe/Warsaw\")", "A -> B: 2023-03-28 12:40:00 CEST Central European Summer Time +0200 +02 +0200 +02:00", "en-US", "UTC", 1680000000000L),
        ("zone_America/Los_Angeles_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"America/Los_Angeles\")", "A -> B: 2023-11-14 14:13:20 PST Pacific Standard Time -0800 -08 -0800 -08:00", "en-US", "UTC", 1700000000000L),
        ("zone_America/Los_Angeles_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"America/Los_Angeles\")", "A -> B: 2023-03-28 03:40:00 PDT Pacific Daylight Time -0700 -07 -0700 -07:00", "en-US", "UTC", 1680000000000L),
        ("zone_Asia/Kathmandu_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"Asia/Kathmandu\")", "A -> B: 2023-11-15 03:58:20 NPT Nepal Time +0545 +05 +0545 +05:45", "en-US", "UTC", 1700000000000L),
        ("zone_Asia/Kathmandu_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"Asia/Kathmandu\")", "A -> B: 2023-03-28 16:25:00 NPT Nepal Time +0545 +05 +0545 +05:45", "en-US", "UTC", 1680000000000L),
        ("zone_Asia/Hong_Kong_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"Asia/Hong_Kong\")", "A -> B: 2023-11-15 06:13:20 HKT Hong Kong Standard Time +0800 +08 +0800 +08:00", "en-US", "UTC", 1700000000000L),
        ("zone_Asia/Hong_Kong_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"Asia/Hong_Kong\")", "A -> B: 2023-03-28 18:40:00 HKT Hong Kong Standard Time +0800 +08 +0800 +08:00", "en-US", "UTC", 1680000000000L),
        ("zone_Pacific/Auckland_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"Pacific/Auckland\")", "A -> B: 2023-11-15 11:13:20 NZDT New Zealand Daylight Time +1300 +13 +1300 +13:00", "en-US", "UTC", 1700000000000L),
        ("zone_Pacific/Auckland_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"Pacific/Auckland\")", "A -> B: 2023-03-28 23:40:00 NZDT New Zealand Daylight Time +1300 +13 +1300 +13:00", "en-US", "UTC", 1680000000000L),
        ("zone_Australia/Lord_Howe_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"Australia/Lord_Howe\")", "A -> B: 2023-11-15 09:13:20 LHDT Lord Howe Daylight Time +1100 +11 +1100 +11:00", "en-US", "UTC", 1700000000000L),
        ("zone_Australia/Lord_Howe_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"Australia/Lord_Howe\")", "A -> B: 2023-03-28 21:40:00 LHDT Lord Howe Daylight Time +1100 +11 +1100 +11:00", "en-US", "UTC", 1680000000000L),
        ("zone_Etc/GMT+5_1700000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1700000000,\"Etc/GMT+5\")", "A -> B: 2023-11-14 17:13:20 GMT-05:00 GMT-05:00 -0500 -05 -0500 -05:00", "en-US", "UTC", 1700000000000L),
        ("zone_Etc/GMT+5_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z X XX XXX\",1680000000,\"Etc/GMT+5\")", "A -> B: 2023-03-28 05:40:00 GMT-05:00 GMT-05:00 -0500 -05 -0500 -05:00", "en-US", "UTC", 1680000000000L),
        ("locale_en-US", "A -> B: %date(\"G GG GGGG | y yyyy M MMM MMMM L LLL LLLL | E EEEE a | D F u w W\",1700000000,\"UTC\")", "A -> B: AD AD AD | 2023 2023 11 Nov November 11 Nov November | Tue Tuesday PM | 318 2 2 46 3", "en-US", "UTC", 1700000000000L),
        ("locale_context_en-US", "A -> B: %date(\"d MMMM yyyy | d LLLL yyyy | MMMM\",1700000000,\"UTC\")", "A -> B: 14 November 2023 | 14 November 2023 | November", "en-US", "UTC", 1700000000000L),
        ("week_en-US_1451606400000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1451606400,\"UTC\")", "A -> B: 2016-01-01 2016-01 1", "en-US", "UTC", 1451606400000L),
        ("week_en-US_1609459200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1609459200,\"UTC\")", "A -> B: 2021-01-01 2021-01 1", "en-US", "UTC", 1609459200000L),
        ("week_en-US_1640995200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1640995200,\"UTC\")", "A -> B: 2022-01-01 2022-01 1", "en-US", "UTC", 1640995200000L),
        ("week_en-US_1735603200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1735603200,\"UTC\")", "A -> B: 2024-12-31 2025-01 5", "en-US", "UTC", 1735603200000L),
        ("week_en-US_1672531200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1672531200,\"UTC\")", "A -> B: 2023-01-01 2023-01 1", "en-US", "UTC", 1672531200000L),
        ("locale_en-GB", "A -> B: %date(\"G GG GGGG | y yyyy M MMM MMMM L LLL LLLL | E EEEE a | D F u w W\",1700000000,\"UTC\")", "A -> B: AD AD AD | 2023 2023 11 Nov November 11 Nov November | Tue Tuesday pm | 318 2 2 46 3", "en-GB", "UTC", 1700000000000L),
        ("locale_context_en-GB", "A -> B: %date(\"d MMMM yyyy | d LLLL yyyy | MMMM\",1700000000,\"UTC\")", "A -> B: 14 November 2023 | 14 November 2023 | November", "en-GB", "UTC", 1700000000000L),
        ("week_en-GB_1451606400000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1451606400,\"UTC\")", "A -> B: 2016-01-01 2015-53 0", "en-GB", "UTC", 1451606400000L),
        ("week_en-GB_1609459200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1609459200,\"UTC\")", "A -> B: 2021-01-01 2020-53 0", "en-GB", "UTC", 1609459200000L),
        ("week_en-GB_1640995200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1640995200,\"UTC\")", "A -> B: 2022-01-01 2021-52 0", "en-GB", "UTC", 1640995200000L),
        ("week_en-GB_1735603200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1735603200,\"UTC\")", "A -> B: 2024-12-31 2025-01 5", "en-GB", "UTC", 1735603200000L),
        ("week_en-GB_1672531200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1672531200,\"UTC\")", "A -> B: 2023-01-01 2022-52 0", "en-GB", "UTC", 1672531200000L),
        ("locale_fr-FR", "A -> B: %date(\"G GG GGGG | y yyyy M MMM MMMM L LLL LLLL | E EEEE a | D F u w W\",1700000000,\"UTC\")", "A -> B: ap. J.-C. ap. J.-C. ap. J.-C. | 2023 2023 11 nov. novembre 11 nov. novembre | mar. mardi PM | 318 2 2 46 3", "fr-FR", "UTC", 1700000000000L),
        ("locale_context_fr-FR", "A -> B: %date(\"d MMMM yyyy | d LLLL yyyy | MMMM\",1700000000,\"UTC\")", "A -> B: 14 novembre 2023 | 14 novembre 2023 | novembre", "fr-FR", "UTC", 1700000000000L),
        ("week_fr-FR_1451606400000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1451606400,\"UTC\")", "A -> B: 2016-01-01 2015-53 0", "fr-FR", "UTC", 1451606400000L),
        ("week_fr-FR_1609459200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1609459200,\"UTC\")", "A -> B: 2021-01-01 2020-53 0", "fr-FR", "UTC", 1609459200000L),
        ("week_fr-FR_1640995200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1640995200,\"UTC\")", "A -> B: 2022-01-01 2021-52 0", "fr-FR", "UTC", 1640995200000L),
        ("week_fr-FR_1735603200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1735603200,\"UTC\")", "A -> B: 2024-12-31 2025-01 5", "fr-FR", "UTC", 1735603200000L),
        ("week_fr-FR_1672531200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1672531200,\"UTC\")", "A -> B: 2023-01-01 2022-52 0", "fr-FR", "UTC", 1672531200000L),
        ("locale_de-DE", "A -> B: %date(\"G GG GGGG | y yyyy M MMM MMMM L LLL LLLL | E EEEE a | D F u w W\",1700000000,\"UTC\")", "A -> B: n. Chr. n. Chr. n. Chr. | 2023 2023 11 Nov. November 11 Nov November | Di. Dienstag PM | 318 2 2 46 3", "de-DE", "UTC", 1700000000000L),
        ("locale_context_de-DE", "A -> B: %date(\"d MMMM yyyy | d LLLL yyyy | MMMM\",1700000000,\"UTC\")", "A -> B: 14 November 2023 | 14 November 2023 | November", "de-DE", "UTC", 1700000000000L),
        ("week_de-DE_1451606400000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1451606400,\"UTC\")", "A -> B: 2016-01-01 2015-53 0", "de-DE", "UTC", 1451606400000L),
        ("week_de-DE_1609459200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1609459200,\"UTC\")", "A -> B: 2021-01-01 2020-53 0", "de-DE", "UTC", 1609459200000L),
        ("week_de-DE_1640995200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1640995200,\"UTC\")", "A -> B: 2022-01-01 2021-52 0", "de-DE", "UTC", 1640995200000L),
        ("week_de-DE_1735603200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1735603200,\"UTC\")", "A -> B: 2024-12-31 2025-01 5", "de-DE", "UTC", 1735603200000L),
        ("week_de-DE_1672531200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1672531200,\"UTC\")", "A -> B: 2023-01-01 2022-52 0", "de-DE", "UTC", 1672531200000L),
        ("locale_pl-PL", "A -> B: %date(\"G GG GGGG | y yyyy M MMM MMMM L LLL LLLL | E EEEE a | D F u w W\",1700000000,\"UTC\")", "A -> B: n.e. n.e. n.e. | 2023 2023 11 lis listopada 11 lis listopad | wt. wtorek PM | 318 2 2 46 3", "pl-PL", "UTC", 1700000000000L),
        ("locale_context_pl-PL", "A -> B: %date(\"d MMMM yyyy | d LLLL yyyy | MMMM\",1700000000,\"UTC\")", "A -> B: 14 listopada 2023 | 14 listopad 2023 | listopada", "pl-PL", "UTC", 1700000000000L),
        ("week_pl-PL_1451606400000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1451606400,\"UTC\")", "A -> B: 2016-01-01 2015-53 0", "pl-PL", "UTC", 1451606400000L),
        ("week_pl-PL_1609459200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1609459200,\"UTC\")", "A -> B: 2021-01-01 2020-53 0", "pl-PL", "UTC", 1609459200000L),
        ("week_pl-PL_1640995200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1640995200,\"UTC\")", "A -> B: 2022-01-01 2021-52 0", "pl-PL", "UTC", 1640995200000L),
        ("week_pl-PL_1735603200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1735603200,\"UTC\")", "A -> B: 2024-12-31 2025-01 5", "pl-PL", "UTC", 1735603200000L),
        ("week_pl-PL_1672531200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1672531200,\"UTC\")", "A -> B: 2023-01-01 2022-52 0", "pl-PL", "UTC", 1672531200000L),
        ("locale_ru-RU", "A -> B: %date(\"G GG GGGG | y yyyy M MMM MMMM L LLL LLLL | E EEEE a | D F u w W\",1700000000,\"UTC\")", "A -> B: н. э. н. э. н. э. | 2023 2023 11 нояб. ноября 11 нояб. ноябрь | вт вторник PM | 318 2 2 46 3", "ru-RU", "UTC", 1700000000000L),
        ("locale_context_ru-RU", "A -> B: %date(\"d MMMM yyyy | d LLLL yyyy | MMMM\",1700000000,\"UTC\")", "A -> B: 14 ноября 2023 | 14 ноябрь 2023 | ноября", "ru-RU", "UTC", 1700000000000L),
        ("week_ru-RU_1451606400000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1451606400,\"UTC\")", "A -> B: 2016-01-01 2015-53 0", "ru-RU", "UTC", 1451606400000L),
        ("week_ru-RU_1609459200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1609459200,\"UTC\")", "A -> B: 2021-01-01 2020-53 0", "ru-RU", "UTC", 1609459200000L),
        ("week_ru-RU_1640995200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1640995200,\"UTC\")", "A -> B: 2022-01-01 2021-52 0", "ru-RU", "UTC", 1640995200000L),
        ("week_ru-RU_1735603200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1735603200,\"UTC\")", "A -> B: 2024-12-31 2025-01 5", "ru-RU", "UTC", 1735603200000L),
        ("week_ru-RU_1672531200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1672531200,\"UTC\")", "A -> B: 2023-01-01 2022-52 0", "ru-RU", "UTC", 1672531200000L),
        ("locale_cs-CZ", "A -> B: %date(\"G GG GGGG | y yyyy M MMM MMMM L LLL LLLL | E EEEE a | D F u w W\",1700000000,\"UTC\")", "A -> B: n. l. n. l. n. l. | 2023 2023 11 lis listopadu 11 lis listopad | út úterý odp. | 318 2 2 46 3", "cs-CZ", "UTC", 1700000000000L),
        ("locale_context_cs-CZ", "A -> B: %date(\"d MMMM yyyy | d LLLL yyyy | MMMM\",1700000000,\"UTC\")", "A -> B: 14 listopadu 2023 | 14 listopad 2023 | listopadu", "cs-CZ", "UTC", 1700000000000L),
        ("week_cs-CZ_1451606400000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1451606400,\"UTC\")", "A -> B: 2016-01-01 2015-53 0", "cs-CZ", "UTC", 1451606400000L),
        ("week_cs-CZ_1609459200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1609459200,\"UTC\")", "A -> B: 2021-01-01 2020-53 0", "cs-CZ", "UTC", 1609459200000L),
        ("week_cs-CZ_1640995200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1640995200,\"UTC\")", "A -> B: 2022-01-01 2021-52 0", "cs-CZ", "UTC", 1640995200000L),
        ("week_cs-CZ_1735603200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1735603200,\"UTC\")", "A -> B: 2024-12-31 2025-01 5", "cs-CZ", "UTC", 1735603200000L),
        ("week_cs-CZ_1672531200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1672531200,\"UTC\")", "A -> B: 2023-01-01 2022-52 0", "cs-CZ", "UTC", 1672531200000L),
        ("locale_es-ES", "A -> B: %date(\"G GG GGGG | y yyyy M MMM MMMM L LLL LLLL | E EEEE a | D F u w W\",1700000000,\"UTC\")", "A -> B: d. C. d. C. d. C. | 2023 2023 11 nov noviembre 11 nov noviembre | mar martes p. m. | 318 2 2 46 3", "es-ES", "UTC", 1700000000000L),
        ("locale_context_es-ES", "A -> B: %date(\"d MMMM yyyy | d LLLL yyyy | MMMM\",1700000000,\"UTC\")", "A -> B: 14 noviembre 2023 | 14 noviembre 2023 | noviembre", "es-ES", "UTC", 1700000000000L),
        ("week_es-ES_1451606400000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1451606400,\"UTC\")", "A -> B: 2016-01-01 2015-53 0", "es-ES", "UTC", 1451606400000L),
        ("week_es-ES_1609459200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1609459200,\"UTC\")", "A -> B: 2021-01-01 2020-53 0", "es-ES", "UTC", 1609459200000L),
        ("week_es-ES_1640995200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1640995200,\"UTC\")", "A -> B: 2022-01-01 2021-52 0", "es-ES", "UTC", 1640995200000L),
        ("week_es-ES_1735603200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1735603200,\"UTC\")", "A -> B: 2024-12-31 2025-01 5", "es-ES", "UTC", 1735603200000L),
        ("week_es-ES_1672531200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1672531200,\"UTC\")", "A -> B: 2023-01-01 2022-52 0", "es-ES", "UTC", 1672531200000L),
        ("locale_it-IT", "A -> B: %date(\"G GG GGGG | y yyyy M MMM MMMM L LLL LLLL | E EEEE a | D F u w W\",1700000000,\"UTC\")", "A -> B: d.C. d.C. d.C. | 2023 2023 11 nov novembre 11 nov novembre | mar martedì PM | 318 2 2 46 3", "it-IT", "UTC", 1700000000000L),
        ("locale_context_it-IT", "A -> B: %date(\"d MMMM yyyy | d LLLL yyyy | MMMM\",1700000000,\"UTC\")", "A -> B: 14 novembre 2023 | 14 novembre 2023 | novembre", "it-IT", "UTC", 1700000000000L),
        ("week_it-IT_1451606400000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1451606400,\"UTC\")", "A -> B: 2016-01-01 2015-53 0", "it-IT", "UTC", 1451606400000L),
        ("week_it-IT_1609459200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1609459200,\"UTC\")", "A -> B: 2021-01-01 2020-53 0", "it-IT", "UTC", 1609459200000L),
        ("week_it-IT_1640995200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1640995200,\"UTC\")", "A -> B: 2022-01-01 2021-52 0", "it-IT", "UTC", 1640995200000L),
        ("week_it-IT_1735603200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1735603200,\"UTC\")", "A -> B: 2024-12-31 2025-01 5", "it-IT", "UTC", 1735603200000L),
        ("week_it-IT_1672531200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1672531200,\"UTC\")", "A -> B: 2023-01-01 2022-52 0", "it-IT", "UTC", 1672531200000L),
        ("locale_ja-JP", "A -> B: %date(\"G GG GGGG | y yyyy M MMM MMMM L LLL LLLL | E EEEE a | D F u w W\",1700000000,\"UTC\")", "A -> B: 西暦 西暦 西暦 | 2023 2023 11 11月 11月 11 11月 11月 | 火 火曜日 午後 | 318 2 2 46 3", "ja-JP", "UTC", 1700000000000L),
        ("locale_context_ja-JP", "A -> B: %date(\"d MMMM yyyy | d LLLL yyyy | MMMM\",1700000000,\"UTC\")", "A -> B: 14 11月 2023 | 14 11月 2023 | 11月", "ja-JP", "UTC", 1700000000000L),
        ("week_ja-JP_1451606400000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1451606400,\"UTC\")", "A -> B: 2016-01-01 2016-01 1", "ja-JP", "UTC", 1451606400000L),
        ("week_ja-JP_1609459200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1609459200,\"UTC\")", "A -> B: 2021-01-01 2021-01 1", "ja-JP", "UTC", 1609459200000L),
        ("week_ja-JP_1640995200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1640995200,\"UTC\")", "A -> B: 2022-01-01 2022-01 1", "ja-JP", "UTC", 1640995200000L),
        ("week_ja-JP_1735603200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1735603200,\"UTC\")", "A -> B: 2024-12-31 2025-01 5", "ja-JP", "UTC", 1735603200000L),
        ("week_ja-JP_1672531200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1672531200,\"UTC\")", "A -> B: 2023-01-01 2023-01 1", "ja-JP", "UTC", 1672531200000L),
        ("locale_zh-CN", "A -> B: %date(\"G GG GGGG | y yyyy M MMM MMMM L LLL LLLL | E EEEE a | D F u w W\",1700000000,\"UTC\")", "A -> B: 公元 公元 公元 | 2023 2023 11 11月 十一月 11 11月 十一月 | 周二 星期二 下午 | 318 2 2 46 3", "zh-CN", "UTC", 1700000000000L),
        ("locale_context_zh-CN", "A -> B: %date(\"d MMMM yyyy | d LLLL yyyy | MMMM\",1700000000,\"UTC\")", "A -> B: 14 十一月 2023 | 14 十一月 2023 | 十一月", "zh-CN", "UTC", 1700000000000L),
        ("week_zh-CN_1451606400000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1451606400,\"UTC\")", "A -> B: 2016-01-01 2016-01 1", "zh-CN", "UTC", 1451606400000L),
        ("week_zh-CN_1609459200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1609459200,\"UTC\")", "A -> B: 2021-01-01 2021-01 1", "zh-CN", "UTC", 1609459200000L),
        ("week_zh-CN_1640995200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1640995200,\"UTC\")", "A -> B: 2022-01-01 2022-01 1", "zh-CN", "UTC", 1640995200000L),
        ("week_zh-CN_1735603200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1735603200,\"UTC\")", "A -> B: 2024-12-31 2025-01 5", "zh-CN", "UTC", 1735603200000L),
        ("week_zh-CN_1672531200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1672531200,\"UTC\")", "A -> B: 2023-01-01 2023-01 1", "zh-CN", "UTC", 1672531200000L),
        ("locale_ko-KR", "A -> B: %date(\"G GG GGGG | y yyyy M MMM MMMM L LLL LLLL | E EEEE a | D F u w W\",1700000000,\"UTC\")", "A -> B: AD AD AD | 2023 2023 11 11월 11월 11 11월 11월 | 화 화요일 오후 | 318 2 2 46 3", "ko-KR", "UTC", 1700000000000L),
        ("locale_context_ko-KR", "A -> B: %date(\"d MMMM yyyy | d LLLL yyyy | MMMM\",1700000000,\"UTC\")", "A -> B: 14 11월 2023 | 14 11월 2023 | 11월", "ko-KR", "UTC", 1700000000000L),
        ("week_ko-KR_1451606400000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1451606400,\"UTC\")", "A -> B: 2016-01-01 2016-01 1", "ko-KR", "UTC", 1451606400000L),
        ("week_ko-KR_1609459200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1609459200,\"UTC\")", "A -> B: 2021-01-01 2021-01 1", "ko-KR", "UTC", 1609459200000L),
        ("week_ko-KR_1640995200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1640995200,\"UTC\")", "A -> B: 2022-01-01 2022-01 1", "ko-KR", "UTC", 1640995200000L),
        ("week_ko-KR_1735603200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1735603200,\"UTC\")", "A -> B: 2024-12-31 2025-01 5", "ko-KR", "UTC", 1735603200000L),
        ("week_ko-KR_1672531200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1672531200,\"UTC\")", "A -> B: 2023-01-01 2023-01 1", "ko-KR", "UTC", 1672531200000L),
        ("locale_ar-EG", "A -> B: %date(\"G GG GGGG | y yyyy M MMM MMMM L LLL LLLL | E EEEE a | D F u w W\",1700000000,\"UTC\")", "A -> B: م م م | ٢٠٢٣ ٢٠٢٣ ١١ نوفمبر نوفمبر ١١ نوفمبر نوفمبر | الثلاثاء الثلاثاء م | ٣١٨ ٢ ٢ ٤٦ ٣", "ar-EG", "UTC", 1700000000000L),
        ("locale_context_ar-EG", "A -> B: %date(\"d MMMM yyyy | d LLLL yyyy | MMMM\",1700000000,\"UTC\")", "A -> B: ١٤ نوفمبر ٢٠٢٣ | ١٤ نوفمبر ٢٠٢٣ | نوفمبر", "ar-EG", "UTC", 1700000000000L),
        ("week_ar-EG_1451606400000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1451606400,\"UTC\")", "A -> B: ٢٠١٦-٠١-٠١ ٢٠١٦-٠١ ١", "ar-EG", "UTC", 1451606400000L),
        ("week_ar-EG_1609459200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1609459200,\"UTC\")", "A -> B: ٢٠٢١-٠١-٠١ ٢٠٢١-٠١ ١", "ar-EG", "UTC", 1609459200000L),
        ("week_ar-EG_1640995200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1640995200,\"UTC\")", "A -> B: ٢٠٢٢-٠١-٠١ ٢٠٢٢-٠١ ١", "ar-EG", "UTC", 1640995200000L),
        ("week_ar-EG_1735603200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1735603200,\"UTC\")", "A -> B: ٢٠٢٤-١٢-٣١ ٢٠٢٥-٠١ ٥", "ar-EG", "UTC", 1735603200000L),
        ("week_ar-EG_1672531200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1672531200,\"UTC\")", "A -> B: ٢٠٢٣-٠١-٠١ ٢٠٢٣-٠١ ١", "ar-EG", "UTC", 1672531200000L),
        ("locale_fa-IR", "A -> B: %date(\"G GG GGGG | y yyyy M MMM MMMM L LLL LLLL | E EEEE a | D F u w W\",1700000000,\"UTC\")", "A -> B: م. م. م. | ۲۰۲۳ ۲۰۲۳ ۱۱ نوامبر نوامبر ۱۱ نوامبر نوامبر | سه‌شنبه سه‌شنبه بعدازظهر | ۳۱۸ ۲ ۲ ۴۶ ۳", "fa-IR", "UTC", 1700000000000L),
        ("locale_context_fa-IR", "A -> B: %date(\"d MMMM yyyy | d LLLL yyyy | MMMM\",1700000000,\"UTC\")", "A -> B: ۱۴ نوامبر ۲۰۲۳ | ۱۴ نوامبر ۲۰۲۳ | نوامبر", "fa-IR", "UTC", 1700000000000L),
        ("week_fa-IR_1451606400000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1451606400,\"UTC\")", "A -> B: ۲۰۱۶-۰۱-۰۱ ۲۰۱۶-۰۱ ۱", "fa-IR", "UTC", 1451606400000L),
        ("week_fa-IR_1609459200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1609459200,\"UTC\")", "A -> B: ۲۰۲۱-۰۱-۰۱ ۲۰۲۱-۰۱ ۱", "fa-IR", "UTC", 1609459200000L),
        ("week_fa-IR_1640995200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1640995200,\"UTC\")", "A -> B: ۲۰۲۲-۰۱-۰۱ ۲۰۲۲-۰۱ ۱", "fa-IR", "UTC", 1640995200000L),
        ("week_fa-IR_1735603200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1735603200,\"UTC\")", "A -> B: ۲۰۲۴-۱۲-۳۱ ۲۰۲۵-۰۱ ۵", "fa-IR", "UTC", 1735603200000L),
        ("week_fa-IR_1672531200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1672531200,\"UTC\")", "A -> B: ۲۰۲۳-۰۱-۰۱ ۲۰۲۳-۰۱ ۱", "fa-IR", "UTC", 1672531200000L),
        ("locale_th-TH", "A -> B: %date(\"G GG GGGG | y yyyy M MMM MMMM L LLL LLLL | E EEEE a | D F u w W\",1700000000,\"UTC\")", "A -> B: พ.ศ. พ.ศ. พุทธศักราช | 2566 2566 11 พ.ย. พฤศจิกายน 11 พ.ย. พฤศจิกายน | อ. วันอังคาร หลังเที่ยง | 318 2 2 46 3", "th-TH", "UTC", 1700000000000L),
        ("locale_context_th-TH", "A -> B: %date(\"d MMMM yyyy | d LLLL yyyy | MMMM\",1700000000,\"UTC\")", "A -> B: 14 พฤศจิกายน 2566 | 14 พฤศจิกายน 2566 | พฤศจิกายน", "th-TH", "UTC", 1700000000000L),
        ("week_th-TH_1451606400000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1451606400,\"UTC\")", "A -> B: 2559-01-01 2559-01 1", "th-TH", "UTC", 1451606400000L),
        ("week_th-TH_1609459200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1609459200,\"UTC\")", "A -> B: 2564-01-01 2564-01 1", "th-TH", "UTC", 1609459200000L),
        ("week_th-TH_1640995200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1640995200,\"UTC\")", "A -> B: 2565-01-01 2565-01 1", "th-TH", "UTC", 1640995200000L),
        ("week_th-TH_1735603200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1735603200,\"UTC\")", "A -> B: 2567-12-31 2568-01 5", "th-TH", "UTC", 1735603200000L),
        ("week_th-TH_1672531200000", "A -> B: %date(\"yyyy-MM-dd YYYY-ww W\",1672531200,\"UTC\")", "A -> B: 2566-01-01 2566-01 1", "th-TH", "UTC", 1672531200000L),
        ("default_UTC_en-US", "A -> B: %date()", "A -> B: Tue Nov 14 22:13:20 UTC 2023", "en-US", "UTC", 1700000000000L),
        ("default_UTC_pl-PL", "A -> B: %date()", "A -> B: Tue Nov 14 22:13:20 UTC 2023", "pl-PL", "UTC", 1700000000000L),
        ("default_Europe/Warsaw_en-US", "A -> B: %date()", "A -> B: Tue Nov 14 23:13:20 CET 2023", "en-US", "Europe/Warsaw", 1700000000000L),
        ("default_Europe/Warsaw_pl-PL", "A -> B: %date()", "A -> B: Tue Nov 14 23:13:20 CET 2023", "pl-PL", "Europe/Warsaw", 1700000000000L),
        ("default_America/Los_Angeles_en-US", "A -> B: %date()", "A -> B: Tue Nov 14 14:13:20 PST 2023", "en-US", "America/Los_Angeles", 1700000000000L),
        ("default_America/Los_Angeles_pl-PL", "A -> B: %date()", "A -> B: Tue Nov 14 14:13:20 PST 2023", "pl-PL", "America/Los_Angeles", 1700000000000L),
        ("default_Asia/Kolkata_en-US", "A -> B: %date()", "A -> B: Wed Nov 15 03:43:20 IST 2023", "en-US", "Asia/Kolkata", 1700000000000L),
        ("default_Asia/Kolkata_pl-PL", "A -> B: %date()", "A -> B: Wed Nov 15 03:43:20 IST 2023", "pl-PL", "Asia/Kolkata", 1700000000000L),
        ("default_Asia/Hong_Kong_en-US", "A -> B: %date()", "A -> B: Wed Nov 15 06:13:20 HKT 2023", "en-US", "Asia/Hong_Kong", 1700000000000L),
        ("default_Asia/Hong_Kong_pl-PL", "A -> B: %date()", "A -> B: Wed Nov 15 06:13:20 HKT 2023", "pl-PL", "Asia/Hong_Kong", 1700000000000L),
        ("default_Australia/Sydney_en-US", "A -> B: %date()", "A -> B: Wed Nov 15 09:13:20 AEDT 2023", "en-US", "Australia/Sydney", 1700000000000L),
        ("default_Australia/Sydney_pl-PL", "A -> B: %date()", "A -> B: Wed Nov 15 09:13:20 AEDT 2023", "pl-PL", "Australia/Sydney", 1700000000000L),
        ("pattern_yyyy '' MM 'quoted' dd", "A -> B: %date(\"yyyy '' MM 'quoted' dd\",1700000000,\"UTC\")", "A -> B: 2023 ' 11 quoted 14", "en-US", "UTC", 1700000000000L),
        ("pattern_yyyy'", "A -> B: %date(\"yyyy'\",1700000000,\"UTC\")", null, "en-US", "UTC", 1700000000000L),
        ("pattern_q", "A -> B: %date(\"q\",1700000000,\"UTC\")", null, "en-US", "UTC", 1700000000000L),
        ("pattern_XXXX", "A -> B: %date(\"XXXX\",1700000000,\"UTC\")", null, "en-US", "UTC", 1700000000000L),
        ("pattern_x", "A -> B: %date(\"x\",1700000000,\"UTC\")", null, "en-US", "UTC", 1700000000000L),
        ("pattern_", "A -> B: %date(\"\",1700000000,\"UTC\")", "A -> B: ", "en-US", "UTC", 1700000000000L),
        ("pattern_yyyy-MM-dd'T'HH:mm:ss.SSSXXX", "A -> B: %date(\"yyyy-MM-dd'T'HH:mm:ss.SSSXXX\",1700000000,\"UTC\")", "A -> B: 2023-11-14T22:13:20.000Z", "en-US", "UTC", 1700000000000L),
        ("invalid_zone_Not/A_Zone", "A -> B: %date(\"yyyy z\",1700000000,\"Not/A_Zone\")", null, "en-US", "UTC", 1700000000000L),
        ("invalid_zone_utc", "A -> B: %date(\"yyyy z\",1700000000,\"utc\")", null, "en-US", "UTC", 1700000000000L),
        ("invalid_zone_europe/warsaw", "A -> B: %date(\"yyyy z\",1700000000,\"europe/warsaw\")", null, "en-US", "UTC", 1700000000000L),
        ("invalid_zone_GMT+01:00", "A -> B: %date(\"yyyy z\",1700000000,\"GMT+01:00\")", null, "en-US", "UTC", 1700000000000L),
        ("invalid_zone_Eastern Standard Time", "A -> B: %date(\"yyyy z\",1700000000,\"Eastern Standard Time\")", null, "en-US", "UTC", 1700000000000L),
        ("invalid_zone_Asia/Kolkata", "A -> B: %date(\"yyyy z\",1700000000,\"Asia/Kolkata\")", "A -> B: 2023 IST", "en-US", "UTC", 1700000000000L),
        ("allzones_Africa/Windhoek_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"Africa/Windhoek\")", "A -> B: 2023-03-28 12:40:00 WAT Western African Time +0200 +02:00", "en-US", "UTC", 1680000000000L),
        ("allzones_Africa/Windhoek_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"Africa/Windhoek\")", "A -> B: 2023-07-01 02:00:00 WAT Western African Time +0200 +02:00", "en-US", "UTC", 1688169600000L),
        ("allzones_Asia/Calcutta_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"Asia/Calcutta\")", "A -> B: 2023-03-28 16:10:00 IST India Standard Time +0530 +05:30", "en-US", "UTC", 1680000000000L),
        ("allzones_Asia/Calcutta_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"Asia/Calcutta\")", "A -> B: 2023-07-01 05:30:00 IST India Standard Time +0530 +05:30", "en-US", "UTC", 1688169600000L),
        ("allzones_Europe/Kiev_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"Europe/Kiev\")", "A -> B: 2023-03-28 13:40:00 EEST Eastern European Summer Time +0300 +03:00", "en-US", "UTC", 1680000000000L),
        ("allzones_Europe/Kiev_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"Europe/Kiev\")", "A -> B: 2023-07-01 03:00:00 EEST Eastern European Summer Time +0300 +03:00", "en-US", "UTC", 1688169600000L),
        ("allzones_SystemV/AST4_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"SystemV/AST4\")", "A -> B: 2023-03-28 06:40:00 AST Atlantic Standard Time -0400 -04:00", "en-US", "UTC", 1680000000000L),
        ("allzones_SystemV/AST4_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"SystemV/AST4\")", "A -> B: 2023-06-30 20:00:00 AST Atlantic Standard Time -0400 -04:00", "en-US", "UTC", 1688169600000L),
        ("allzones_SystemV/AST4ADT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"SystemV/AST4ADT\")", "A -> B: 2023-03-28 06:40:00 AST Atlantic Standard Time -0400 -04:00", "en-US", "UTC", 1680000000000L),
        ("allzones_SystemV/AST4ADT_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"SystemV/AST4ADT\")", "A -> B: 2023-06-30 21:00:00 ADT Atlantic Daylight Time -0300 -03:00", "en-US", "UTC", 1688169600000L),
        ("allzones_SystemV/CST6_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"SystemV/CST6\")", "A -> B: 2023-03-28 04:40:00 CST Central Standard Time -0600 -06:00", "en-US", "UTC", 1680000000000L),
        ("allzones_SystemV/CST6_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"SystemV/CST6\")", "A -> B: 2023-06-30 18:00:00 CST Central Standard Time -0600 -06:00", "en-US", "UTC", 1688169600000L),
        ("allzones_SystemV/CST6CDT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"SystemV/CST6CDT\")", "A -> B: 2023-03-28 04:40:00 CST Central Standard Time -0600 -06:00", "en-US", "UTC", 1680000000000L),
        ("allzones_SystemV/CST6CDT_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"SystemV/CST6CDT\")", "A -> B: 2023-06-30 19:00:00 CDT Central Daylight Time -0500 -05:00", "en-US", "UTC", 1688169600000L),
        ("allzones_SystemV/EST5_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"SystemV/EST5\")", "A -> B: 2023-03-28 05:40:00 EST Eastern Standard Time -0500 -05:00", "en-US", "UTC", 1680000000000L),
        ("allzones_SystemV/EST5_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"SystemV/EST5\")", "A -> B: 2023-06-30 19:00:00 EST Eastern Standard Time -0500 -05:00", "en-US", "UTC", 1688169600000L),
        ("allzones_SystemV/EST5EDT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"SystemV/EST5EDT\")", "A -> B: 2023-03-28 05:40:00 EST Eastern Standard Time -0500 -05:00", "en-US", "UTC", 1680000000000L),
        ("allzones_SystemV/EST5EDT_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"SystemV/EST5EDT\")", "A -> B: 2023-06-30 20:00:00 EDT Eastern Daylight Time -0400 -04:00", "en-US", "UTC", 1688169600000L),
        ("allzones_SystemV/HST10_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"SystemV/HST10\")", "A -> B: 2023-03-28 00:40:00 HST Hawaii-Aleutian Standard Time -1000 -10:00", "en-US", "UTC", 1680000000000L),
        ("allzones_SystemV/HST10_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"SystemV/HST10\")", "A -> B: 2023-06-30 14:00:00 HST Hawaii-Aleutian Standard Time -1000 -10:00", "en-US", "UTC", 1688169600000L),
        ("allzones_SystemV/MST7_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"SystemV/MST7\")", "A -> B: 2023-03-28 03:40:00 MST Mountain Standard Time -0700 -07:00", "en-US", "UTC", 1680000000000L),
        ("allzones_SystemV/MST7_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"SystemV/MST7\")", "A -> B: 2023-06-30 17:00:00 MST Mountain Standard Time -0700 -07:00", "en-US", "UTC", 1688169600000L),
        ("allzones_SystemV/MST7MDT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"SystemV/MST7MDT\")", "A -> B: 2023-03-28 03:40:00 MST Mountain Standard Time -0700 -07:00", "en-US", "UTC", 1680000000000L),
        ("allzones_SystemV/MST7MDT_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"SystemV/MST7MDT\")", "A -> B: 2023-06-30 18:00:00 MDT Mountain Daylight Time -0600 -06:00", "en-US", "UTC", 1688169600000L),
        ("allzones_SystemV/PST8_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"SystemV/PST8\")", "A -> B: 2023-03-28 02:40:00 PST Pitcairn Time -0800 -08:00", "en-US", "UTC", 1680000000000L),
        ("allzones_SystemV/PST8_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"SystemV/PST8\")", "A -> B: 2023-06-30 16:00:00 PST Pitcairn Time -0800 -08:00", "en-US", "UTC", 1688169600000L),
        ("allzones_SystemV/PST8PDT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"SystemV/PST8PDT\")", "A -> B: 2023-03-28 02:40:00 PST Pacific Standard Time -0800 -08:00", "en-US", "UTC", 1680000000000L),
        ("allzones_SystemV/PST8PDT_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"SystemV/PST8PDT\")", "A -> B: 2023-06-30 17:00:00 PDT Pacific Daylight Time -0700 -07:00", "en-US", "UTC", 1688169600000L),
        ("allzones_SystemV/YST9_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"SystemV/YST9\")", "A -> B: 2023-03-28 01:40:00 AKST Gambier Time -0900 -09:00", "en-US", "UTC", 1680000000000L),
        ("allzones_SystemV/YST9_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"SystemV/YST9\")", "A -> B: 2023-06-30 15:00:00 AKST Gambier Time -0900 -09:00", "en-US", "UTC", 1688169600000L),
        ("allzones_SystemV/YST9YDT_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"SystemV/YST9YDT\")", "A -> B: 2023-03-28 01:40:00 AKST Alaska Standard Time -0900 -09:00", "en-US", "UTC", 1680000000000L),
        ("allzones_SystemV/YST9YDT_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"SystemV/YST9YDT\")", "A -> B: 2023-06-30 16:00:00 AKDT Alaska Daylight Time -0800 -08:00", "en-US", "UTC", 1688169600000L),
        ("allzones_US/Eastern_1680000000000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1680000000,\"US/Eastern\")", "A -> B: 2023-03-28 06:40:00 EDT Eastern Daylight Time -0400 -04:00", "en-US", "UTC", 1680000000000L),
        ("allzones_US/Eastern_1688169600000", "A -> B: %date(\"yyyy-MM-dd HH:mm:ss z zzzz Z XXX\",1688169600,\"US/Eastern\")", "A -> B: 2023-06-30 20:00:00 EDT Eastern Daylight Time -0400 -04:00", "en-US", "UTC", 1688169600000L),
        ("localized_0_fr-FR", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Europe/Paris\")", "A -> B: CET | heure normale d’Europe centrale | +0100", "fr-FR", "UTC", 1673740800000L),
        ("localized_1_fr-FR", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Europe/Paris\")", "A -> B: CEST | heure d’été d’Europe centrale | +0200", "fr-FR", "UTC", 1689379200000L),
        ("localized_2_fr-CA", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"America/Toronto\")", "A -> B: HNE | heure normale de l’Est | -0500", "fr-CA", "UTC", 1673740800000L),
        ("localized_3_fr-CA", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"America/Toronto\")", "A -> B: HAE | heure avancée de l’Est | -0400", "fr-CA", "UTC", 1689379200000L),
        ("localized_4_de-DE", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Europe/Berlin\")", "A -> B: MEZ | Mitteleuropäische Normalzeit | +0100", "de-DE", "UTC", 1673740800000L),
        ("localized_5_de-DE", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Europe/Berlin\")", "A -> B: MESZ | Mitteleuropäische Sommerzeit | +0200", "de-DE", "UTC", 1689379200000L),
        ("localized_6_pl-PL", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Europe/Warsaw\")", "A -> B: CET | czas środkowoeuropejski standardowy | +0100", "pl-PL", "UTC", 1673740800000L),
        ("localized_7_pl-PL", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Europe/Warsaw\")", "A -> B: CEST | czas środkowoeuropejski letni | +0200", "pl-PL", "UTC", 1689379200000L),
        ("localized_8_es-MX", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"America/Mexico_City\")", "A -> B: CST | hora estándar central | -0600", "es-MX", "UTC", 1673740800000L),
        ("localized_9_es-MX", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"America/Mexico_City\")", "A -> B: CST | hora estándar central | -0600", "es-MX", "UTC", 1689379200000L),
        ("localized_10_pt-BR", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"America/Sao_Paulo\")", "A -> B: BRT | Horário Padrão de Brasília | -0300", "pt-BR", "UTC", 1673740800000L),
        ("localized_11_pt-BR", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"America/Sao_Paulo\")", "A -> B: BRT | Horário Padrão de Brasília | -0300", "pt-BR", "UTC", 1689379200000L),
        ("localized_12_ar-EG", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Africa/Cairo\")", "A -> B: EET | توقيت شرق أوروبا الرسمي | +0200", "ar-EG", "UTC", 1673740800000L),
        ("localized_13_ar-EG", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Africa/Cairo\")", "A -> B: EEST | توقيت شرق أوروبا الصيفي | +0300", "ar-EG", "UTC", 1689379200000L),
        ("localized_14_fa-IR", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Asia/Tehran\")", "A -> B: IRST | وقت عادی ایران | +0330", "fa-IR", "UTC", 1673740800000L),
        ("localized_15_fa-IR", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Asia/Tehran\")", "A -> B: IRST | وقت عادی ایران | +0330", "fa-IR", "UTC", 1689379200000L),
        ("localized_16_he-IL", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Asia/Jerusalem\")", "A -> B: IST | שעון ישראל (חורף) | +0200", "he-IL", "UTC", 1673740800000L),
        ("localized_17_he-IL", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Asia/Jerusalem\")", "A -> B: IDT | שעון ישראל (קיץ) | +0300", "he-IL", "UTC", 1689379200000L),
        ("localized_18_iw-IL", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Asia/Jerusalem\")", "A -> B: IST | שעון ישראל (חורף) | +0200", "iw-IL", "UTC", 1673740800000L),
        ("localized_19_iw-IL", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Asia/Jerusalem\")", "A -> B: IDT | שעון ישראל (קיץ) | +0300", "iw-IL", "UTC", 1689379200000L),
        ("localized_20_zh-CN", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Asia/Shanghai\")", "A -> B: CST | 中国标准时间 | +0800", "zh-CN", "UTC", 1673740800000L),
        ("localized_21_zh-CN", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Asia/Shanghai\")", "A -> B: CST | 中国标准时间 | +0800", "zh-CN", "UTC", 1689379200000L),
        ("localized_22_zh-TW", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Asia/Taipei\")", "A -> B: TST | 台北標準時間 | +0800", "zh-TW", "UTC", 1673740800000L),
        ("localized_23_zh-TW", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Asia/Taipei\")", "A -> B: TST | 台北標準時間 | +0800", "zh-TW", "UTC", 1689379200000L),
        ("localized_24_zh-HK", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Asia/Hong_Kong\")", "A -> B: HKT | 香港標準時間 | +0800", "zh-HK", "UTC", 1673740800000L),
        ("localized_25_zh-HK", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Asia/Hong_Kong\")", "A -> B: HKT | 香港標準時間 | +0800", "zh-HK", "UTC", 1689379200000L),
        ("localized_26_ja-JP", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Asia/Tokyo\")", "A -> B: JST | 日本標準時 | +0900", "ja-JP", "UTC", 1673740800000L),
        ("localized_27_ja-JP", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Asia/Tokyo\")", "A -> B: JST | 日本標準時 | +0900", "ja-JP", "UTC", 1689379200000L),
        ("localized_28_ko-KR", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Asia/Seoul\")", "A -> B: KST | 대한민국 표준시 | +0900", "ko-KR", "UTC", 1673740800000L),
        ("localized_29_ko-KR", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Asia/Seoul\")", "A -> B: KST | 대한민국 표준시 | +0900", "ko-KR", "UTC", 1689379200000L),
        ("localized_30_hi-IN", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Asia/Kolkata\")", "A -> B: IST | भारतीय मानक समय | +0530", "hi-IN", "UTC", 1673740800000L),
        ("localized_31_hi-IN", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Asia/Kolkata\")", "A -> B: IST | भारतीय मानक समय | +0530", "hi-IN", "UTC", 1689379200000L),
        ("localized_32_th-TH", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Asia/Bangkok\")", "A -> B: ICT | เวลาอินโดจีน | +0700", "th-TH", "UTC", 1673740800000L),
        ("localized_33_th-TH", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Asia/Bangkok\")", "A -> B: ICT | เวลาอินโดจีน | +0700", "th-TH", "UTC", 1689379200000L),
        ("localized_34_ru-RU", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Europe/Moscow\")", "A -> B: MSK | Москва, стандартное время | +0300", "ru-RU", "UTC", 1673740800000L),
        ("localized_35_ru-RU", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Europe/Moscow\")", "A -> B: MSK | Москва, стандартное время | +0300", "ru-RU", "UTC", 1689379200000L),
        ("localized_36_uk-UA", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Europe/Kyiv\")", "A -> B: EET | за східноєвропейським стандартним часом | +0200", "uk-UA", "UTC", 1673740800000L),
        ("localized_37_uk-UA", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Europe/Kyiv\")", "A -> B: EEST | за східноєвропейським літнім часом | +0300", "uk-UA", "UTC", 1689379200000L),
        ("localized_38_sr-Cyrl-RS", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Europe/Belgrade\")", "A -> B: CET | Средњеевропско стандардно време | +0100", "sr-Cyrl-RS", "UTC", 1673740800000L),
        ("localized_39_sr-Cyrl-RS", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Europe/Belgrade\")", "A -> B: CEST | Средњеевропско летње време | +0200", "sr-Cyrl-RS", "UTC", 1689379200000L),
        ("localized_40_sr-Latn-RS", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Europe/Belgrade\")", "A -> B: CET | Srednjeevropsko standardno vreme | +0100", "sr-Latn-RS", "UTC", 1673740800000L),
        ("localized_41_sr-Latn-RS", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Europe/Belgrade\")", "A -> B: CEST | Srednjeevropsko letnje vreme | +0200", "sr-Latn-RS", "UTC", 1689379200000L),
        ("localized_42_en-GB", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Europe/London\")", "A -> B: GMT | Greenwich Mean Time | +0000", "en-GB", "UTC", 1673740800000L),
        ("localized_43_en-GB", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Europe/London\")", "A -> B: BST | British Summer Time | +0100", "en-GB", "UTC", 1689379200000L),
        ("localized_44_en-AU", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Australia/Sydney\")", "A -> B: AEDT | Australian Eastern Daylight Time | +1100", "en-AU", "UTC", 1673740800000L),
        ("localized_45_en-AU", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Australia/Sydney\")", "A -> B: AEST | Australian Eastern Standard Time | +1000", "en-AU", "UTC", 1689379200000L),
        ("localized_46_ga-IE", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Europe/Dublin\")", "A -> B: MAG | Meán-Am Greenwich | +0000", "ga-IE", "UTC", 1673740800000L),
        ("localized_47_ga-IE", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Europe/Dublin\")", "A -> B: ACÉ | Am Caighdéanach na hÉireann | +0100", "ga-IE", "UTC", 1689379200000L),
        ("localized_48_cy-GB", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Europe/London\")", "A -> B: GMT | Amser Safonol Greenwich | +0000", "cy-GB", "UTC", 1673740800000L),
        ("localized_49_cy-GB", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Europe/London\")", "A -> B: BST | Amser Haf Prydain | +0100", "cy-GB", "UTC", 1689379200000L),
        ("localized_50_fr-XX", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"UTC\")", "A -> B: UTC | Temps universel coordonné | +0000", "fr-XX", "UTC", 1673740800000L),
        ("localized_51_fr-XX", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"UTC\")", "A -> B: UTC | Temps universel coordonné | +0000", "fr-XX", "UTC", 1689379200000L),
        ("localized_52_zh-Hant-XX", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Asia/Taipei\")", "A -> B: TST | 台北標準時間 | +0800", "zh-Hant-XX", "UTC", 1673740800000L),
        ("localized_53_zh-Hant-XX", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Asia/Taipei\")", "A -> B: TST | 台北標準時間 | +0800", "zh-Hant-XX", "UTC", 1689379200000L),
        ("localized_54_ff-Adlm-GN", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"Africa/Conakry\")", "A -> B: GMT | 𞤑𞤭𞤶𞤮𞥅𞤪𞤫 𞤖𞤢𞤳𞤭𞤲𞤭𞥅𞤲𞥋𞤣𞤫 𞤘𞤪𞤭𞤲𞤱𞤭𞥅𞤧 | +0000", "ff-Adlm-GN", "UTC", 1673740800000L),
        ("localized_55_ff-Adlm-GN", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"Africa/Conakry\")", "A -> B: GMT | 𞤑𞤭𞤶𞤮𞥅𞤪𞤫 𞤖𞤢𞤳𞤭𞤲𞤭𞥅𞤲𞥋𞤣𞤫 𞤘𞤪𞤭𞤲𞤱𞤭𞥅𞤧 | +0000", "ff-Adlm-GN", "UTC", 1689379200000L),
        ("localized_default", "A -> B: %date()", "A -> B: Sat Jul 15 02:00:00 CEST 2023", "fr-FR", "Europe/Paris", 1689379200000L),
        ("localized_legacy_0", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"AET\")", "A -> B: AEDT | 東部夏時間(ニューサウスウェールズ) | +1100", "ja-JP-u-ca-japanese-x-lvariant-JP", "UTC", 1673740800000L),
        ("localized_legacy_1", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"AET\")", "A -> B: AEST | 東部標準時(ニューサウスウェールズ) | +1000", "ja-JP-u-ca-japanese-x-lvariant-JP", "UTC", 1689379200000L),
        ("localized_legacy_2", "A -> B: %date(\"z | zzzz | Z\",1673740800,\"AET\")", "A -> B: AEDT | Australian Eastern Daylight Time (New South Wales) | +1100", "th-TH-u-nu-thai-x-lvariant-TH", "UTC", 1673740800000L),
        ("localized_legacy_3", "A -> B: %date(\"z | zzzz | Z\",1689379200,\"AET\")", "A -> B: AEST | Australian Eastern Standard Time (New South Wales) | +1000", "th-TH-u-nu-thai-x-lvariant-TH", "UTC", 1689379200000L),
    ];

    private static void CheckDates(PlantUmlRenderOptions options)
    {
        foreach (var item in DateCases)
        {
            var instant = DateTimeOffset.FromUnixTimeMilliseconds(item.Instant);
            var configured = options with
            {
                Culture = CultureInfo.GetCultureInfo(item.Culture),
                TimeZone = TimeZoneInfo.FindSystemTimeZoneById(item.Zone),
                UtcNow = () => instant,
                SourceFileModifiedAt = instant
            };
            if (item.Expected == null)
            {
                if (PlantUmlPreprocessor.TryProcess("@startuml\n" + item.Source + "\n@enduml", out _, configured))
                    throw new Exception("Invalid date builtin accepted " + item.Name);
            }
            else
            {
                Expect(item.Source, item.Expected, configured, item.Name);
                if (item.Source.Contains("%date()", StringComparison.Ordinal))
                    Expect(item.Source.Replace("%date()", "%filedate()", StringComparison.Ordinal), item.Expected, configured, item.Name + " file date");
            }
        }
        Expect("A -> B: %filedate()", "A -> B: ", options with { SourceFileModifiedAt = null }, "Missing file date");
        Expect("A -> B: %date(\"" + new string('y', 80) + "\",1700000000,\"UTC\")", "A -> B: " + "2023".PadLeft(80, '0'), options, "Long date field width");
    }

    internal static void Run()
    {
        const string context = """
            !$object={"a":1,"nested":{"x":2},"list":[1,2],"flag":true,"nil":null}
            !$array=["a","b",3]
            !$keys=[{"a":1},{"b":2},{"a":3}]
            !$other={"a":5,"nested":{"y":3},"b":7}
            """;
        var options = new PlantUmlRenderOptions
        {
            SourceFileName = "models/main.puml",
            SourceFileModifiedAt = DateTimeOffset.FromUnixTimeSeconds(1700000000),
            TimeZone = TimeZoneInfo.Utc,
            Culture = CultureInfo.InvariantCulture,
            UtcNow = () => DateTimeOffset.FromUnixTimeSeconds(1700000000),
            ResolveEnvironment = name => name == "DEMO" ? "example" : null,
            RandomInteger = (minimum, maximum) => minimum,
            Arguments = "-test -quiet",
            Resources = new(new Dictionary<string, string>
            {
                ["models/data.json"] = "{\"a\":42}",
                ["models/parts/name.puml"] = "A -> B: %filename() %dirpath()"
            })
        };
        CheckDates(options);
        CheckIrishZoneNames(options);
        foreach (var item in Cases)
            Expect(context + "\nA -> B: " + item.Expression, "A -> B: " + item.Expected, options, item.Name);

        Expect("A -> B: %now() / %filename() / %filename_no_extension() / %dirpath() / %getenv(\"DEMO\") / %getenv(\"missing\") / %xargs(\"\")",
            "A -> B: 1700000000 / main.puml / main / models / example /  / -test -quiet", options, "Host capabilities");
        Expect("A -> B: %file_exists(\"data.json\") / %file_exists(\"none.json\") / %load_json(\"data.json\")",
            "A -> B: 1 / 0 / {\"a\":42}", options, "Relative JSON resources");
        Expect("!include parts/name.puml", "A -> B: name.puml models/parts", options, "Included file metadata");
        Expect("A -> B: %get_stdlib(\"awslib\",\"version\") / %get_stdlib(\"material\",\"name\")",
            "A -> B: 14.0.0 / material2.1.19", options, "Standard library aliases");
        Expect("A -> B: %get_variable_value(\"missing\") / %get_current_theme()", "A -> B:  / {}", options, "Missing variables");
        Expect("A -> B: %lighten(\"transparent\",10) / %is_dark(\"transparent\") / %reverse_color(\"transparent\") / %reverse_hsluv_color(\"transparent\")",
            "A -> B: #000000 / 1 / #FFFFFF / #767676", options, "Transparent color transforms");
        foreach (string expression in new[]
        {
            "%upper()", "%lower(1,2)", "%intval(\"bad\")", "%mod(1,0)", "%substr(\"abc\",-1)",
            "%random(0)", "%random(2,1)", "%date(\"yyyy\",0,\"Not/A_Zone\")", "%date(\"unknown\",0)",
            "%load_json(\"missing\",\"broken json\")", "%splitstr_regex(\"abc\",\"[\")", "%reverse_color(\"none\")", "%reverse_color(\"#1234\")"
        })
        {
            if (PlantUmlPreprocessor.TryProcess("@startuml\nA -> B: " + expression + "\n@enduml", out _, options))
                throw new Exception("Invalid builtin accepted " + expression);
        }
        Parallel.For(0, 24, index =>
        {
            var isolated = options with { ResolveEnvironment = _ => index.ToString(CultureInfo.InvariantCulture) };
            Expect("A -> B: %getenv(\"DEMO\")", "A -> B: " + index, isolated, "Concurrent host isolation");
        });
    }

    private static void CheckIrishZoneNames(PlantUmlRenderOptions options)
    {
        // Frozen OpenJDK 21 SimpleDateFormat results. Dublin's 1968–71 permanent
        // +01 period also uses IST in Java, although TZDB marks it as standard.
        var cases = new (string Instant, bool Summer)[]
        {
            ("1968-01-15T00:00:00Z", false), ("1968-07-15T00:00:00Z", true),
            ("1968-12-15T00:00:00Z", true), ("1970-01-15T00:00:00Z", true),
            ("1971-10-31T01:59:59Z", true), ("1971-10-31T02:00:00Z", false),
            ("2023-01-15T00:00:00Z", false), ("2023-07-15T00:00:00Z", true),
            ("2023-03-26T00:59:59Z", false), ("2023-03-26T01:00:00Z", true),
            ("2023-10-29T00:59:59Z", true), ("2023-10-29T01:00:00Z", false)
        };
        foreach (string zone in new[] { "Europe/Dublin", "Eire" })
        foreach (string locale in new[] { "en-US", "ga-IE" })
        foreach (var item in cases)
        {
            var instant = DateTimeOffset.Parse(item.Instant, CultureInfo.InvariantCulture);
            string names = locale == "en-US" ? item.Summer ? "IST | Irish Standard Time" : "GMT | Greenwich Mean Time" :
                item.Summer ? "ACÉ | Am Caighdéanach na hÉireann" : "MAG | Meán-Am Greenwich";
            string expected = "A -> B: " + names + (item.Summer ? " | +0100" : " | +0000");
            var configured = options with { Culture = CultureInfo.GetCultureInfo(locale), TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin") };
            string seconds = instant.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            Expect("A -> B: %date(\"z | zzzz | Z\"," + seconds + ",\"" + zone + "\")", expected, configured, "Irish explicit zone " + zone + " " + item.Instant + " " + locale);
            Expect("A -> B: %date(\"z | zzzz | Z\"," + seconds + ")", expected, configured, "Irish default zone " + zone + " " + item.Instant + " " + locale);
        }
        // Exercise both positive and negative DST encodings on every host.
        // This catches the bug even on CI machines whose zoneinfo uses rearguard rules.
        var spring = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 1, 0, 0), 3, 5, DayOfWeek.Sunday);
        var autumn = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 2, 0, 0), 10, 5, DayOfWeek.Sunday);
        foreach (bool negative in new[] { false, true })
        foreach (string identifier in new[] { "Europe/Dublin", "Eire" })
        {
            var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2020, 1, 1), new(2030, 12, 31), TimeSpan.FromHours(negative ? -1 : 1), negative ? autumn : spring, negative ? spring : autumn);
            var zone = TimeZoneInfo.CreateCustomTimeZone(identifier, TimeSpan.FromHours(negative ? 1 : 0), "Ireland", "GMT", "IST", [rule]);
            foreach (var (seconds, expected) in new[] { (1673740800L, "GMT | Greenwich Mean Time | +0000"), (1689379200L, "IST | Irish Standard Time | +0100") })
                Expect("A -> B: %date(\"z | zzzz | Z\"," + seconds.ToString(CultureInfo.InvariantCulture) + ")", "A -> B: " + expected,
                    options with { Culture = CultureInfo.GetCultureInfo("en-US"), TimeZone = zone }, "Irish " + identifier + " " + (negative ? "negative" : "positive") + " DST encoding");
        }
    }

    private static void Expect(string body, string expected, PlantUmlRenderOptions options, string name)
    {
        if (!PlantUmlPreprocessor.TryProcess("@startuml\n" + body + "\n@enduml", out var result, options))
            throw new Exception("Builtin expansion declined " + name);
        string actual = string.Join('\n', result.Source.Split('\n').Where(line => line.Length > 0 && !line.StartsWith('@')));
        if (actual != expected) throw new Exception("Builtin mismatch " + name + " expected " + expected + " got " + actual);
    }
}
