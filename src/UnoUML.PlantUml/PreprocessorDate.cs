using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace UnoUML.PlantUml;

internal static partial class PreprocessorDate
{
    internal static string Format(DateTimeOffset instant, string? pattern, TimeZoneInfo zone, CultureInfo culture, string? zoneId = null)
    {
        var time = TimeZoneInfo.ConvertTime(instant, zone);
        if (pattern == null)
            return time.ToString("ddd MMM dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + ZoneName(time, zone, false, zoneId) + " " + time.Year.ToString(CultureInfo.InvariantCulture);
        var format = culture.DateTimeFormat;
        // SimpleDateFormat uses Gregorian dates for Persian and Arabic locales.
        // The Thai locale retains its Buddhist year numbering.
        if (format.Calendar is not (GregorianCalendar or ThaiBuddhistCalendar))
        {
            format = (DateTimeFormatInfo)format.Clone();
            format.Calendar = new GregorianCalendar();
        }
        int year = format.Calendar.GetYear(time.DateTime);
        bool contextualMonth = HasMultipleFields(pattern);
        string[] nativeDigits = culture.NumberFormat.NativeDigits;
        var output = new StringBuilder(pattern.Length + 16);
        bool quoted = false;
        for (int index = 0; index < pattern.Length;)
        {
            char token = pattern[index++];
            if (token == '\'')
            {
                if (index < pattern.Length && pattern[index] == '\'') { output.Append('\''); index++; }
                else quoted = !quoted;
                continue;
            }
            if (quoted || !char.IsAsciiLetter(token)) { output.Append(token); continue; }
            int count = 1;
            while (index < pattern.Length && pattern[index] == token) { count++; index++; }
            int number;
            switch (token)
            {
                case 'G':
                    output.Append(culture.Equals(CultureInfo.InvariantCulture) || culture.TwoLetterISOLanguageName == "en" ? "AD" :
                        format.Calendar is ThaiBuddhistCalendar && count >= 4 ? "พุทธศักราช" : format.GetEraName(format.Calendar.GetEra(time.DateTime)));
                    continue;
                case 'y': number = year; if (count == 2) number %= 100; break;
                case 'Y': number = WeekYear(time.DateTime, culture) + year - time.Year; if (count == 2) number %= 100; break;
                case 'M':
                case 'L':
                    if (count >= 4) { output.Append(token == 'M' && contextualMonth ? format.MonthGenitiveNames[time.Month - 1] : format.GetMonthName(time.Month)); continue; }
                    if (count == 3) { output.Append(token == 'M' && contextualMonth ? format.AbbreviatedMonthGenitiveNames[time.Month - 1] : format.GetAbbreviatedMonthName(time.Month)); continue; }
                    number = time.Month; break;
                case 'w': number = (time.Date - WeekStart(WeekYear(time.DateTime, culture), 1, culture)).Days / 7 + 1; break;
                case 'W': number = (int)Math.Floor((time.Date - WeekStart(time.Year, time.Month, culture)).Days / 7d) + 1; break;
                case 'D': number = time.DayOfYear; break;
                case 'd': number = time.Day; break;
                case 'F': number = (time.Day - 1) / 7 + 1; break;
                case 'E':
                    string dayName = count >= 4 ? format.GetDayName(time.DayOfWeek) : format.GetAbbreviatedDayName(time.DayOfWeek);
                    output.Append(dayName);
                    if (count < 4 && culture.TwoLetterISOLanguageName == "de" && !dayName.EndsWith('.')) output.Append('.');
                    continue;
                case 'u': number = time.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)time.DayOfWeek; break;
                case 'a': output.Append(time.Hour < 12 ? format.AMDesignator : format.PMDesignator); continue;
                case 'H': number = time.Hour; break;
                case 'k': number = time.Hour == 0 ? 24 : time.Hour; break;
                case 'K': number = time.Hour % 12; break;
                case 'h': number = time.Hour % 12 == 0 ? 12 : time.Hour % 12; break;
                case 'm': number = time.Minute; break;
                case 's': number = time.Second; break;
                case 'S': number = time.Millisecond; break;
                case 'z': output.Append(ZoneName(time, zone, count >= 4, zoneId, culture)); continue;
                case 'Z': output.Append(Offset(time.Offset, false, false)); continue;
                case 'X':
                    if (count > 3) throw new FormatException("Invalid ISO timezone pattern.");
                    output.Append(time.Offset == TimeSpan.Zero ? "Z" : Offset(time.Offset, count == 3, count == 1)); continue;
                default: throw new FormatException("Unsupported date pattern letter.");
            }
            string digits = number.ToString(CultureInfo.InvariantCulture).PadLeft(count, '0');
            foreach (char digit in digits) output.Append(nativeDigits[digit - '0']);
        }
        if (quoted) throw new FormatException("Unterminated date pattern literal.");
        return output.ToString();
    }

    private static bool HasMultipleFields(string pattern)
    {
        bool quoted = false, found = false;
        for (int index = 0; index < pattern.Length; index++)
        {
            char current = pattern[index];
            if (current == '\'') { quoted = !quoted; continue; }
            if (quoted || !char.IsAsciiLetter(current)) continue;
            if (found) return true;
            found = true;
            while (index + 1 < pattern.Length && pattern[index + 1] == current) index++;
        }
        return false;
    }

    private static int WeekYear(DateTime value, CultureInfo culture)
    {
        if (value.Date < WeekStart(value.Year, 1, culture)) return value.Year - 1;
        return value.Year < 9999 && value.Date >= WeekStart(value.Year + 1, 1, culture) ? value.Year + 1 : value.Year;
    }

    private static DateTime WeekStart(int year, int month, CultureInfo culture)
    {
        var first = new DateTime(year, month, 1);
        var firstDay = culture.Name.EndsWith("-CN", StringComparison.OrdinalIgnoreCase) ? DayOfWeek.Sunday : culture.DateTimeFormat.FirstDayOfWeek;
        int before = ((int)first.DayOfWeek - (int)firstDay + 7) % 7;
        int minimum = culture.DateTimeFormat.CalendarWeekRule switch { CalendarWeekRule.FirstFourDayWeek => 4, CalendarWeekRule.FirstFullWeek => 7, _ => 1 };
        return first.AddDays(7 - before < minimum ? 7 - before : -before);
    }

    private static string Offset(TimeSpan offset, bool colon, bool hoursOnly)
    {
        var absolute = offset.Duration();
        string result = (offset < TimeSpan.Zero ? "-" : "+") + ((int)absolute.TotalHours).ToString("00", CultureInfo.InvariantCulture);
        if (!hoursOnly) result += (colon ? ":" : "") + absolute.Minutes.ToString("00", CultureInfo.InvariantCulture);
        return result;
    }

    private static string ZoneName(DateTimeOffset time, TimeZoneInfo zone, bool full, string? zoneId, CultureInfo? culture = null)
    {
        string identifier = zoneId ?? zone.Id;
        if (!ZoneNames.ContainsKey(identifier) && TimeZoneInfo.TryConvertWindowsIdToIanaId(identifier, out string? iana)) identifier = iana;
        bool daylight = zone.IsDaylightSavingTime(time);
        // Java retains Namibia's one-hour standard offset after permanent
        // summer time, so its display-name selection differs from CLR rules.
        if (identifier == "Africa/Windhoek") daylight = time.Offset == TimeSpan.FromHours(2);
        if (ZoneNames.TryGetValue(identifier, out var names))
        {
            if (culture != null) names = LocalizedNames(identifier, culture) ?? names;
            return full ? daylight ? names.DaylightFull : names.StandardFull : daylight ? names.Daylight : names.Standard;
        }
        if (full) return daylight ? zone.DaylightName : zone.StandardName;
        return "GMT" + Offset(time.Offset, true, false);
    }

    internal static TimeZoneInfo ResolveZone(string identifier)
    {
        if (!ZoneNames.ContainsKey(identifier)) throw new TimeZoneNotFoundException("Unknown time zone.");
        if (identifier.StartsWith("SystemV/", StringComparison.Ordinal)) return SystemVZone(identifier);
        if (identifier is "EST" or "MST" or "HST")
        {
            int hours = identifier == "EST" ? -5 : identifier == "MST" ? -7 : -10;
            return TimeZoneInfo.CreateCustomTimeZone(identifier, TimeSpan.FromHours(hours), identifier, identifier);
        }
        if (ZoneAliases.TryGetValue(identifier, out string? target)) identifier = target;
        return TimeZoneInfo.FindSystemTimeZoneById(identifier);
    }

    private static TimeZoneInfo SystemVZone(string identifier)
    {
        int hours = identifier == "SystemV/HST10" ? 10 : identifier[11] - '0';
        var offset = TimeSpan.FromHours(-hours);
        if (!identifier.EndsWith("DT", StringComparison.Ordinal))
            return TimeZoneInfo.CreateCustomTimeZone(identifier, offset, identifier, identifier);
        var time = new DateTime(1, 1, 1, 2, 0, 0);
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(time, 4, 5, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(time, 10, 5, DayOfWeek.Sunday);
        TimeZoneInfo.AdjustmentRule[] rules =
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(1900, 1, 1), new(1973, 12, 31), TimeSpan.FromHours(1), start, end),
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(1974, 1, 1), new(1974, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(time, 1, 6), TimeZoneInfo.TransitionTime.CreateFixedDateRule(time, 11, 24)),
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(1975, 1, 1), new(1975, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(time, 2, 23), end),
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(1976, 1, 1), new(9999, 12, 31), TimeSpan.FromHours(1), start, end)
        ];
        return TimeZoneInfo.CreateCustomTimeZone(identifier, offset, identifier, identifier, identifier, rules);
    }

    private sealed record Names(string Standard, string Daylight, string StandardFull, string DaylightFull);
    private static readonly FrozenDictionary<string, Names> ZoneNames = ReadZoneNames();
    private static readonly FrozenDictionary<string, string> ZoneAliases = ReadZoneAliases();

    private static FrozenDictionary<string, Names> ReadZoneNames()
    {
        var result = new Dictionary<string, Names>(StringComparer.Ordinal);
        foreach (string row in ZoneNameData.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = row.Split('|');
            var names = new Names(fields[1], fields[2], fields[3], fields[4]);
            foreach (string identifier in fields[0].Split(',')) result.Add(identifier, names);
        }
        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static FrozenDictionary<string, string> ReadZoneAliases()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string row in ZoneAliasData.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = row.Split('|');
            foreach (string identifier in fields[0].Split(',')) result.Add(identifier, fields[1]);
        }
        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    // Stable, deduplicated display-name and alias data from the Java 17 reference
    // runtime used by PlantUML 1.2026.9. Date arithmetic uses CLR timezone rules.
    private const string ZoneNameData = """
        ACT,Australia/Adelaide,Australia/Broken_Hill,Australia/Darwin,Australia/North,Australia/South,Australia/Yancowinna|ACST|ACDT|Australian Central Standard Time|Australian Central Daylight Time
        AET,Antarctica/Macquarie,Australia/ACT,Australia/Brisbane,Australia/Canberra,Australia/Currie,Australia/Hobart,Australia/Lindeman,Australia/Melbourne,Australia/NSW,Australia/Queensland,Australia/Sydney,Australia/Tasmania,Australia/Victoria|AEST|AEDT|Australian Eastern Standard Time|Australian Eastern Daylight Time
        AGT|ART|ARST|Argentine Time|Argentine Summer Time
        ART,Africa/Cairo,Africa/Tripoli,Asia/Amman,Asia/Beirut,Asia/Damascus,Asia/Gaza,Asia/Hebron,Asia/Nicosia,Egypt,Europe/Athens,Europe/Bucharest,Europe/Chisinau,Europe/Helsinki,Europe/Kaliningrad,Europe/Kiev,Europe/Kyiv,Europe/Mariehamn,Europe/Nicosia,Europe/Riga,Europe/Sofia,Europe/Tallinn,Europe/Tiraspol,Europe/Uzhgorod,Europe/Vilnius,Europe/Zaporozhye,Libya|EET|EEST|Eastern European Standard Time|Eastern European Summer Time
        AST,America/Anchorage,America/Juneau,America/Metlakatla,America/Nome,America/Sitka,America/Yakutat,SystemV/YST9YDT,US/Alaska|AKST|AKDT|Alaska Standard Time|Alaska Daylight Time
        Africa/Abidjan,Africa/Bamako,Africa/Banjul,Africa/Bissau,Africa/Conakry,Africa/Dakar,Africa/Freetown,Africa/Lome,Africa/Monrovia,Africa/Nouakchott,Africa/Ouagadougou,Africa/Sao_Tome,Africa/Timbuktu,America/Danmarkshavn,Atlantic/Reykjavik,Atlantic/St_Helena,Etc/GMT,Etc/GMT+0,Etc/GMT-0,Etc/GMT0,Etc/Greenwich,GMT,GMT0,Greenwich,Iceland|GMT|GMT|Greenwich Mean Time|Greenwich Mean Time
        Africa/Accra|GMT|GHST|Greenwich Mean Time|Ghana Summer Time
        Africa/Addis_Ababa,Africa/Asmara,Africa/Asmera,Africa/Dar_es_Salaam,Africa/Djibouti,Africa/Kampala,Africa/Mogadishu,Africa/Nairobi,EAT,Indian/Antananarivo,Indian/Comoro,Indian/Mayotte|EAT|EAST|East Africa Time|Eastern African Summer Time
        Africa/Algiers,Africa/Ceuta,Africa/Tunis,Arctic/Longyearbyen,Atlantic/Jan_Mayen,ECT,Europe/Amsterdam,Europe/Andorra,Europe/Belgrade,Europe/Berlin,Europe/Bratislava,Europe/Brussels,Europe/Budapest,Europe/Busingen,Europe/Copenhagen,Europe/Gibraltar,Europe/Ljubljana,Europe/Luxembourg,Europe/Madrid,Europe/Malta,Europe/Monaco,Europe/Oslo,Europe/Paris,Europe/Podgorica,Europe/Prague,Europe/Rome,Europe/San_Marino,Europe/Sarajevo,Europe/Skopje,Europe/Stockholm,Europe/Tirane,Europe/Vaduz,Europe/Vatican,Europe/Vienna,Europe/Warsaw,Europe/Zagreb,Europe/Zurich,Poland|CET|CEST|Central European Standard Time|Central European Summer Time
        Africa/Bangui,Africa/Brazzaville,Africa/Douala,Africa/Kinshasa,Africa/Lagos,Africa/Libreville,Africa/Luanda,Africa/Malabo,Africa/Ndjamena,Africa/Niamey,Africa/Porto-Novo|WAT|WAST|West Africa Standard Time|West Africa Summer Time
        Africa/Blantyre,Africa/Bujumbura,Africa/Gaborone,Africa/Harare,Africa/Juba,Africa/Khartoum,Africa/Kigali,Africa/Lubumbashi,Africa/Lusaka,Africa/Maputo,CAT|CAT|CAST|Central Africa Time|Central African Summer Time
        Africa/Casablanca,Africa/El_Aaiun,WET|WET|WEST|Western European Time|Western European Summer Time
        Africa/Johannesburg,Africa/Maseru,Africa/Mbabane|SAST|SAST|South Africa Standard Time|South Africa Summer Time
        Africa/Windhoek|CAT|WAT|Central Africa Time|Western African Time
        America/Adak,America/Atka,Pacific/Johnston,US/Aleutian|HAST|HADT|Hawaii-Aleutian Standard Time|Hawaii-Aleutian Daylight Time
        America/Anguilla,America/Antigua,America/Aruba,America/Barbados,America/Blanc-Sablon,America/Curacao,America/Dominica,America/Glace_Bay,America/Goose_Bay,America/Grenada,America/Guadeloupe,America/Halifax,America/Kralendijk,America/Lower_Princes,America/Marigot,America/Martinique,America/Moncton,America/Montserrat,America/Port_of_Spain,America/Puerto_Rico,America/Santo_Domingo,America/St_Barthelemy,America/St_Kitts,America/St_Lucia,America/St_Thomas,America/St_Vincent,America/Thule,America/Tortola,America/Virgin,Atlantic/Bermuda,Canada/Atlantic,PRT,SystemV/AST4,SystemV/AST4ADT|AST|ADT|Atlantic Standard Time|Atlantic Daylight Time
        America/Araguaina,America/Bahia,America/Belem,America/Fortaleza,America/Maceio,America/Recife,America/Santarem,America/Sao_Paulo,BET,Brazil/East|BRT|BRST|Brasilia Standard Time|Brasilia Summer Time
        America/Argentina/Buenos_Aires,America/Argentina/Catamarca,America/Argentina/ComodRivadavia,America/Argentina/Cordoba,America/Argentina/Jujuy,America/Argentina/La_Rioja,America/Argentina/Mendoza,America/Argentina/Rio_Gallegos,America/Argentina/Salta,America/Argentina/San_Juan,America/Argentina/San_Luis,America/Argentina/Tucuman,America/Argentina/Ushuaia,America/Buenos_Aires,America/Catamarca,America/Cordoba,America/Jujuy,America/Mendoza,America/Rosario|ART|ARST|Argentina Standard Time|Argentina Summer Time
        America/Asuncion|PYT|PYST|Paraguay Standard Time|Paraguay Summer Time
        America/Atikokan,America/Cancun,America/Cayman,America/Coral_Harbour,America/Detroit,America/Fort_Wayne,America/Grand_Turk,America/Indiana/Indianapolis,America/Indiana/Marengo,America/Indiana/Petersburg,America/Indiana/Vevay,America/Indiana/Vincennes,America/Indiana/Winamac,America/Indianapolis,America/Iqaluit,America/Jamaica,America/Kentucky/Louisville,America/Kentucky/Monticello,America/Louisville,America/Montreal,America/Nassau,America/New_York,America/Nipigon,America/Panama,America/Pangnirtung,America/Port-au-Prince,America/Thunder_Bay,America/Toronto,Canada/Eastern,EST5EDT,IET,Jamaica,SystemV/EST5,SystemV/EST5EDT,US/East-Indiana,US/Eastern,US/Michigan|EST|EDT|Eastern Standard Time|Eastern Daylight Time
        America/Bahia_Banderas,America/Belize,America/Chicago,America/Chihuahua,America/Costa_Rica,America/El_Salvador,America/Guatemala,America/Indiana/Knox,America/Indiana/Tell_City,America/Knox_IN,America/Managua,America/Matamoros,America/Menominee,America/Merida,America/Mexico_City,America/Monterrey,America/North_Dakota/Beulah,America/North_Dakota/Center,America/North_Dakota/New_Salem,America/Ojinaga,America/Rainy_River,America/Rankin_Inlet,America/Regina,America/Resolute,America/Swift_Current,America/Tegucigalpa,America/Winnipeg,CST,CST6CDT,Canada/Central,Canada/Saskatchewan,Mexico/General,SystemV/CST6,SystemV/CST6CDT,US/Central,US/Indiana-Starke|CST|CDT|Central Standard Time|Central Daylight Time
        America/Boa_Vista,America/Campo_Grande,America/Cuiaba,America/Manaus,America/Porto_Velho,Brazil/West|AMT|AMST|Amazon Standard Time|Amazon Summer Time
        America/Bogota|COT|COST|Colombia Standard Time|Colombia Summer Time
        America/Boise,America/Cambridge_Bay,America/Ciudad_Juarez,America/Creston,America/Dawson,America/Dawson_Creek,America/Denver,America/Edmonton,America/Fort_Nelson,America/Inuvik,America/Phoenix,America/Shiprock,America/Whitehorse,America/Yellowknife,Canada/Mountain,Canada/Yukon,MST7MDT,Navajo,PNT,SystemV/MST7,SystemV/MST7MDT,US/Arizona,US/Mountain|MST|MDT|Mountain Standard Time|Mountain Daylight Time
        America/Caracas|VET|VEST|Venezuela Time|Venezuela Summer Time
        America/Cayenne|GFT|GFST|French Guiana Time|French Guiana Summer Time
        America/Coyhaique|GMT-03:00|GMT-03:00|Coyhaique Standard Time|Coyhaique Daylight Time
        America/Eirunepe,America/Porto_Acre,America/Rio_Branco,Brazil/Acre|ACT|ACST|Acre Standard Time|Acre Summer Time
        America/Ensenada,America/Los_Angeles,America/Tijuana,America/Vancouver,Canada/Pacific,Mexico/BajaNorte,PST,PST8PDT,SystemV/PST8PDT,US/Pacific|PST|PDT|Pacific Standard Time|Pacific Daylight Time
        America/Godthab,America/Nuuk|WGT|WGST|West Greenland Standard Time|West Greenland Summer Time
        America/Guayaquil|ECT|ECST|Ecuador Time|Ecuador Summer Time
        America/Guyana|GYT|GYST|Guyana Time|Guyana Summer Time
        America/Havana,Cuba|CST|CDT|Cuba Standard Time|Cuba Daylight Time
        America/Hermosillo,America/Mazatlan,Mexico/BajaSur|MST|MDT|Mexican Pacific Standard Time|Mexican Pacific Daylight Time
        America/La_Paz|BOT|BOST|Bolivia Time|Bolivia Summer Time
        America/Lima|PET|PEST|Peru Standard Time|Peru Summer Time
        America/Miquelon|PMST|PMDT|St. Pierre & Miquelon Standard Time|St. Pierre & Miquelon Daylight Time
        America/Montevideo|UYT|UYST|Uruguay Standard Time|Uruguay Summer Time
        America/Noronha,Brazil/DeNoronha|FNT|FNST|Fernando de Noronha Standard Time|Fernando de Noronha Summer Time
        America/Paramaribo|SRT|SRST|Suriname Time|Suriname Summer Time
        America/Punta_Arenas|GMT-03:00|GMT-03:00|Punta Arenas Standard Time|Punta Arenas Standard Time
        America/Santa_Isabel|PST|PDT|Northwest Mexico Standard Time|Northwest Mexico Daylight Time
        America/Santiago,Chile/Continental|CLT|CLST|Chile Standard Time|Chile Summer Time
        America/Scoresbysund|EGT|EGST|East Greenland Standard Time|East Greenland Summer Time
        America/St_Johns,CNT,Canada/Newfoundland|NST|NDT|Newfoundland Standard Time|Newfoundland Daylight Time
        Antarctica/Casey|AWST|AWDT|Casey Time|Australian Western Daylight Time
        Antarctica/Davis|DAVT|DAVST|Davis Time|Davis Summer Time
        Antarctica/DumontDUrville|DDUT|DDUST|Dumont-d’Urville Time|Dumont-d'Urville Summer Time
        Antarctica/Mawson|MAWT|MAWST|Mawson Time|Mawson Summer Time
        Antarctica/McMurdo,Antarctica/South_Pole,NST,NZ,Pacific/Auckland|NZST|NZDT|New Zealand Standard Time|New Zealand Daylight Time
        Antarctica/Palmer|CLT|CLST|Chile Time|Chile Summer Time
        Antarctica/Rothera|ROTT|ROTST|Rothera Time|Rothera Summer Time
        Antarctica/Syowa|SYOT|SYOST|Syowa Time|Syowa Summer Time
        Antarctica/Troll|GMT|CEST|Greenwich Mean Time|Central European Summer Time
        Antarctica/Vostok|VOST|VOSST|Vostok Time|Vostok Summer Time
        Asia/Aden,Asia/Baghdad,Asia/Bahrain,Asia/Kuwait,Asia/Qatar,Asia/Riyadh|AST|ADT|Arabian Standard Time|Arabian Daylight Time
        Asia/Almaty|ALMT|ALMST|East Kazakhstan Time|Alma-Ata Summer Time
        Asia/Anadyr|ANAT|ANAST|Anadyr Standard Time|Anadyr Summer Time
        Asia/Aqtau|AQTT|AQTST|West Kazakhstan Time|Aqtau Summer Time
        Asia/Aqtobe|AQTT|AQTST|West Kazakhstan Time|Aqtobe Summer Time
        Asia/Ashgabat,Asia/Ashkhabad|TMT|TMST|Turkmenistan Standard Time|Turkmenistan Summer Time
        Asia/Atyrau|GMT+05:00|GMT+05:00|West Kazakhstan Time|Atyrau Daylight Time
        Asia/Baku|AZT|AZST|Azerbaijan Standard Time|Azerbaijan Summer Time
        Asia/Bangkok,Asia/Ho_Chi_Minh,Asia/Phnom_Penh,Asia/Saigon,Asia/Vientiane,VST|ICT|ICST|Indochina Time|Indochina Summer Time
        Asia/Barnaul|GMT+07:00|GMT+07:00|Barnaul Standard Time|Barnaul Daylight Time
        Asia/Bishkek|KGT|KGST|Kyrgyzstan Time|Kirgizstan Summer Time
        Asia/Brunei|BNT|BNST|Brunei Darussalam Time|Brunei Summer Time
        Asia/Calcutta,Asia/Colombo,Asia/Kolkata,IST|IST|IDT|India Standard Time|India Daylight Time
        Asia/Chita,Asia/Khandyga,Asia/Yakutsk|YAKT|YAKST|Yakutsk Standard Time|Yakutsk Summer Time
        Asia/Choibalsan|CHOT|CHOST|Ulaanbaatar Standard Time|Ulaanbaatar Summer Time
        Asia/Chongqing,Asia/Chungking,Asia/Harbin,Asia/Macao,Asia/Macau,Asia/Shanghai,CTT,PRC|CST|CDT|China Standard Time|China Daylight Time
        Asia/Dacca,Asia/Dhaka,BST|BDT|BDST|Bangladesh Standard Time|Bangladesh Summer Time
        Asia/Dili|TLT|TLST|East Timor Time|Timor-Leste Summer Time
        Asia/Dubai,Asia/Muscat|GST|GDT|Gulf Standard Time|Gulf Daylight Time
        Asia/Dushanbe|TJT|TJST|Tajikistan Time|Tajikistan Summer Time
        Asia/Famagusta,EET|EET|EEST|Eastern European Time|Eastern European Summer Time
        Asia/Hong_Kong,Hongkong|HKT|HKST|Hong Kong Standard Time|Hong Kong Summer Time
        Asia/Hovd|HOVT|HOVST|Hovd Standard Time|Hovd Summer Time
        Asia/Irkutsk|IRKT|IRKST|Irkutsk Standard Time|Irkutsk Summer Time
        Asia/Istanbul,Europe/Istanbul,Turkey|TRT|TRST|Turkey Time|Turkey Summer Time
        Asia/Jakarta,Asia/Pontianak|WIB|WIST|Western Indonesia Time|West Indonesia Summer Time
        Asia/Jayapura|WIT|EIST|Eastern Indonesia Time|East Indonesia Summer Time
        Asia/Jerusalem,Asia/Tel_Aviv,Israel|IST|IDT|Israel Standard Time|Israel Daylight Time
        Asia/Kabul|AFT|AFST|Afghanistan Time|Afghanistan Summer Time
        Asia/Kamchatka|PETT|PETST|Petropavlovsk-Kamchatski Standard Time|Petropavlovsk-Kamchatski Summer Time
        Asia/Karachi,PLT|PKT|PKST|Pakistan Standard Time|Pakistan Summer Time
        Asia/Kashgar,Asia/Urumqi|XJT|XJDT|Xinjiang Standard Time|Xinjiang Daylight Time
        Asia/Kathmandu,Asia/Katmandu|NPT|NPST|Nepal Time|Nepal Summer Time
        Asia/Krasnoyarsk,Asia/Novokuznetsk|KRAT|KRAST|Krasnoyarsk Standard Time|Krasnoyarsk Summer Time
        Asia/Kuala_Lumpur,Asia/Kuching|MYT|MYST|Malaysia Time|Malaysia Summer Time
        Asia/Magadan|MAGT|MAGST|Magadan Standard Time|Magadan Summer Time
        Asia/Makassar,Asia/Ujung_Pandang|WITA|CIST|Central Indonesia Time|Central Indonesia Summer Time
        Asia/Manila|PST|PDT|Philippine Standard Time|Philippine Summer Time
        Asia/Novosibirsk|NOVT|NOVST|Novosibirsk Standard Time|Novosibirsk Summer Time
        Asia/Omsk|OMST|OMSST|Omsk Standard Time|Omsk Summer Time
        Asia/Oral|ORAT|ORAST|West Kazakhstan Time|Oral Summer Time
        Asia/Pyongyang,Asia/Seoul,ROK|KST|KDT|Korean Standard Time|Korean Daylight Time
        Asia/Qostanay|QOST|QOSST|East Kazakhstan Time|Kostanay Summer Time
        Asia/Qyzylorda|QYZT|QYZST|West Kazakhstan Time|Qyzylorda Summer Time
        Asia/Rangoon,Asia/Yangon|MMT|MMST|Myanmar Time|Myanmar Summer Time
        Asia/Sakhalin|SAKT|SAKST|Sakhalin Standard Time|Sakhalin Summer Time
        Asia/Samarkand,Asia/Tashkent|UZT|UZST|Uzbekistan Standard Time|Uzbekistan Summer Time
        Asia/Singapore,Singapore|SGT|SGST|Singapore Standard Time|Singapore Summer Time
        Asia/Srednekolymsk|SRET|SREDT|Srednekolymsk Time|Srednekolymsk Daylight Time
        Asia/Taipei|CST|CDT|Taipei Standard Time|Taipei Daylight Time
        Asia/Tbilisi|GET|GEST|Georgia Standard Time|Georgia Summer Time
        Asia/Tehran,Iran|IRST|IRDT|Iran Standard Time|Iran Daylight Time
        Asia/Thimbu,Asia/Thimphu|BTT|BTST|Bhutan Time|Bhutan Summer Time
        Asia/Tokyo,JST,Japan|JST|JDT|Japan Standard Time|Japan Daylight Time
        Asia/Tomsk|GMT+07:00|GMT+07:00|Tomsk Standard Time|Tomsk Daylight Time
        Asia/Ulaanbaatar,Asia/Ulan_Bator|ULAT|ULAST|Ulaanbaatar Standard Time|Ulaanbaatar Summer Time
        Asia/Ust-Nera,Asia/Vladivostok|VLAT|VLAST|Vladivostok Standard Time|Vladivostok Summer Time
        Asia/Yekaterinburg|YEKT|YEKST|Yekaterinburg Standard Time|Yekaterinburg Summer Time
        Asia/Yerevan,NET|AMT|AMST|Armenia Standard Time|Armenia Summer Time
        Atlantic/Azores|AZOT|AZOST|Azores Standard Time|Azores Summer Time
        Atlantic/Canary,Atlantic/Faeroe,Atlantic/Faroe,Atlantic/Madeira,Europe/Lisbon,Portugal|WET|WEST|Western European Standard Time|Western European Summer Time
        Atlantic/Cape_Verde|CVT|CVST|Cape Verde Standard Time|Cape Verde Summer Time
        Atlantic/South_Georgia|GST|GDT|South Georgia Time|South Georgia Daylight Time
        Atlantic/Stanley|FKT|FKST|Falkland Islands Standard Time|Falkland Islands Summer Time
        Australia/Eucla|ACWST|ACWDT|Australian Central Western Standard Time|Australian Central Western Daylight Time
        Australia/LHI,Australia/Lord_Howe|LHST|LHDT|Lord Howe Standard Time|Lord Howe Daylight Time
        Australia/Perth,Australia/West|AWST|AWDT|Australian Western Standard Time|Australian Western Daylight Time
        CET|CET|CEST|Central European Time|Central European Summer Time
        Chile/EasterIsland,Pacific/Easter|EAST|EASST|Easter Island Standard Time|Easter Island Summer Time
        EST|EST|EST|Eastern Standard Time|Eastern Standard Time
        Eire,Europe/Dublin|GMT|IST|Greenwich Mean Time|Irish Standard Time
        Etc/GMT+10|GMT-10:00|GMT-10:00|GMT-10:00|GMT-10:00
        Etc/GMT+11|GMT-11:00|GMT-11:00|GMT-11:00|GMT-11:00
        Etc/GMT+12|GMT-12:00|GMT-12:00|GMT-12:00|GMT-12:00
        Etc/GMT+1|GMT-01:00|GMT-01:00|GMT-01:00|GMT-01:00
        Etc/GMT+2|GMT-02:00|GMT-02:00|GMT-02:00|GMT-02:00
        Etc/GMT+3|GMT-03:00|GMT-03:00|GMT-03:00|GMT-03:00
        Etc/GMT+4|GMT-04:00|GMT-04:00|GMT-04:00|GMT-04:00
        Etc/GMT+5|GMT-05:00|GMT-05:00|GMT-05:00|GMT-05:00
        Etc/GMT+6|GMT-06:00|GMT-06:00|GMT-06:00|GMT-06:00
        Etc/GMT+7|GMT-07:00|GMT-07:00|GMT-07:00|GMT-07:00
        Etc/GMT+8|GMT-08:00|GMT-08:00|GMT-08:00|GMT-08:00
        Etc/GMT+9|GMT-09:00|GMT-09:00|GMT-09:00|GMT-09:00
        Etc/GMT-10|GMT+10:00|GMT+10:00|GMT+10:00|GMT+10:00
        Etc/GMT-11|GMT+11:00|GMT+11:00|GMT+11:00|GMT+11:00
        Etc/GMT-12|GMT+12:00|GMT+12:00|GMT+12:00|GMT+12:00
        Etc/GMT-13|GMT+13:00|GMT+13:00|GMT+13:00|GMT+13:00
        Etc/GMT-14|GMT+14:00|GMT+14:00|GMT+14:00|GMT+14:00
        Etc/GMT-1|GMT+01:00|GMT+01:00|GMT+01:00|GMT+01:00
        Etc/GMT-2|GMT+02:00|GMT+02:00|GMT+02:00|GMT+02:00
        Etc/GMT-3|GMT+03:00|GMT+03:00|GMT+03:00|GMT+03:00
        Etc/GMT-4|GMT+04:00|GMT+04:00|GMT+04:00|GMT+04:00
        Etc/GMT-5|GMT+05:00|GMT+05:00|GMT+05:00|GMT+05:00
        Etc/GMT-6|GMT+06:00|GMT+06:00|GMT+06:00|GMT+06:00
        Etc/GMT-7|GMT+07:00|GMT+07:00|GMT+07:00|GMT+07:00
        Etc/GMT-8|GMT+08:00|GMT+08:00|GMT+08:00|GMT+08:00
        Etc/GMT-9|GMT+09:00|GMT+09:00|GMT+09:00|GMT+09:00
        Etc/UCT,Etc/UTC,Etc/Universal,Etc/Zulu,UCT,UTC,Universal,Zulu|UTC|UTC|Coordinated Universal Time|Coordinated Universal Time
        Europe/Astrakhan|GMT+04:00|GMT+04:00|Astrakhan Standard Time|Astrakhan Standard Time
        Europe/Belfast,Europe/Guernsey,Europe/Isle_of_Man,Europe/Jersey,Europe/London,GB,GB-Eire|GMT|BST|Greenwich Mean Time|British Summer Time
        Europe/Kirov|MSK|MSD|Moscow Standard Time|Moscow Daylight Time
        Europe/Minsk,Europe/Moscow,Europe/Simferopol,W-SU|MSK|MSD|Moscow Standard Time|Moscow Summer Time
        Europe/Samara|SAMT|SAMST|Samara Standard Time|Samara Summer Time
        Europe/Saratov|GMT+04:00|GMT+04:00|Saratov Standard Time|Saratov Standard Time
        Europe/Ulyanovsk|GMT+04:00|GMT+04:00|Ulyanovsk Standard Time|Ulyanovsk Standard Time
        Europe/Volgograd|MSK|MSD|Volgograd Standard Time|Volgograd Summer Time
        HST|HST|HST|Hawaii Standard Time|Hawaii Standard Time
        Indian/Chagos|IOT|IOST|Indian Ocean Time|Indian Ocean Territory Summer Time
        Indian/Christmas|CXT|CXST|Christmas Island Time|Christmas Island Summer Time
        Indian/Cocos|CCT|CCST|Cocos Islands Time|Cocos Islands Summer Time
        Indian/Kerguelen|TFT|TFST|French Southern & Antarctic Time|French Southern & Antarctic Lands Summer Time
        Indian/Mahe|SCT|SCST|Seychelles Time|Seychelles Summer Time
        Indian/Maldives|MVT|MVST|Maldives Time|Maldives Summer Time
        Indian/Mauritius|MUT|MUST|Mauritius Standard Time|Mauritius Summer Time
        Indian/Reunion|RET|REST|Réunion Time|Reunion Summer Time
        Kwajalein,Pacific/Kwajalein,Pacific/Majuro|MHT|MHST|Marshall Islands Time|Marshall Islands Summer Time
        MET|MET|MEST|Middle Europe Time|Middle Europe Summer Time
        MIT,Pacific/Apia|WSST|WSDT|Apia Standard Time|Apia Daylight Time
        MST|MST|MST|Mountain Standard Time|Mountain Standard Time
        NZ-CHAT,Pacific/Chatham|CHAST|CHADT|Chatham Standard Time|Chatham Daylight Time
        Pacific/Bougainville|BST|BST|Bougainville Standard Time|Bougainville Daylight Time
        Pacific/Chuuk,Pacific/Truk,Pacific/Yap|CHUT|CHUST|Chuuk Time|Chuuk Summer Time
        Pacific/Efate|VUT|VUST|Vanuatu Standard Time|Vanuatu Summer Time
        Pacific/Enderbury|PHOT|PHOST|Phoenix Islands Time|Phoenix Is. Summer Time
        Pacific/Fakaofo|TKT|TKST|Tokelau Time|Tokelau Summer Time
        Pacific/Fiji|FJT|FJST|Fiji Standard Time|Fiji Summer Time
        Pacific/Funafuti|TVT|TVST|Tuvalu Time|Tuvalu Summer Time
        Pacific/Galapagos|GALT|GALST|Galapagos Time|Galapagos Summer Time
        Pacific/Gambier|GAMT|GAMST|Gambier Time|Gambier Summer Time
        Pacific/Guadalcanal,SST|SBT|SBST|Solomon Islands Time|Solomon Is. Summer Time
        Pacific/Guam,Pacific/Saipan|ChST|ChDT|Chamorro Standard Time|Chamorro Daylight Time
        Pacific/Honolulu,SystemV/HST10,US/Hawaii|HST|HDT|Hawaii-Aleutian Standard Time|Hawaii-Aleutian Daylight Time
        Pacific/Kanton|GMT+13:00|GMT+13:00|Kanton Standard Time|Kanton Daylight Time
        Pacific/Kiritimati|LINT|LINST|Line Islands Time|Line Is. Summer Time
        Pacific/Kosrae|KOST|KOSST|Kosrae Time|Kosrae Summer Time
        Pacific/Marquesas|MART|MARST|Marquesas Time|Marquesas Summer Time
        Pacific/Midway,Pacific/Pago_Pago,Pacific/Samoa,US/Samoa|SST|SDT|Samoa Standard Time|Samoa Daylight Time
        Pacific/Nauru|NRT|NRST|Nauru Time|Nauru Summer Time
        Pacific/Niue|NUT|NUST|Niue Time|Niue Summer Time
        Pacific/Norfolk|NFT|NFST|Norfolk Island Standard Time|Norfolk Island Daylight Time
        Pacific/Noumea|NCT|NCST|New Caledonia Standard Time|New Caledonia Summer Time
        Pacific/Palau|PWT|PWST|Palau Time|Palau Summer Time
        Pacific/Pitcairn|PST|PDT|Pitcairn Time|Pitcairn Daylight Time
        Pacific/Pohnpei,Pacific/Ponape|PONT|PONST|Ponape Time|Pohnpei Summer Time
        Pacific/Port_Moresby|PGT|PGST|Papua New Guinea Time|Papua New Guinea Summer Time
        Pacific/Rarotonga|CKT|CKHST|Cook Islands Standard Time|Cook Islands Half Summer Time
        Pacific/Tahiti|TAHT|TAHST|Tahiti Time|Tahiti Summer Time
        Pacific/Tarawa|GILT|GILST|Gilbert Islands Time|Gilbert Is. Summer Time
        Pacific/Tongatapu|TOT|TOST|Tonga Standard Time|Tonga Summer Time
        Pacific/Wake|WAKT|WAKST|Wake Island Time|Wake Summer Time
        Pacific/Wallis|WFT|WFST|Wallis & Futuna Time|Wallis & Futuna Summer Time
        SystemV/PST8|PST|PST|Pitcairn Time|Pitcairn Time
        SystemV/YST9|AKST|AKST|Gambier Time|Gambier Time
        """;

    private const string ZoneAliasData = """
        ACT,Australia/North|Australia/Darwin
        AET,Australia/ACT,Australia/Canberra,Australia/NSW|Australia/Sydney
        AGT,America/Buenos_Aires|America/Argentina/Buenos_Aires
        ART,Egypt|Africa/Cairo
        AST,US/Alaska|America/Anchorage
        Africa/Accra,Africa/Bamako,Africa/Banjul,Africa/Conakry,Africa/Dakar,Africa/Freetown,Africa/Lome,Africa/Nouakchott,Africa/Ouagadougou,Africa/Timbuktu,Atlantic/Reykjavik,Atlantic/St_Helena,Iceland|Africa/Abidjan
        Africa/Addis_Ababa,Africa/Asmara,Africa/Asmera,Africa/Dar_es_Salaam,Africa/Djibouti,Africa/Kampala,Africa/Mogadishu,Indian/Antananarivo,Indian/Comoro,Indian/Mayotte|Africa/Nairobi
        Africa/Bangui,Africa/Brazzaville,Africa/Douala,Africa/Kinshasa,Africa/Libreville,Africa/Luanda,Africa/Malabo,Africa/Niamey,Africa/Porto-Novo|Africa/Lagos
        Africa/Blantyre,Africa/Bujumbura,Africa/Gaborone,Africa/Harare,Africa/Kigali,Africa/Lubumbashi,Africa/Lusaka|Africa/Maputo
        Africa/Maseru,Africa/Mbabane|Africa/Johannesburg
        America/Anguilla,America/Antigua,America/Aruba,America/Blanc-Sablon,America/Curacao,America/Dominica,America/Grenada,America/Guadeloupe,America/Kralendijk,America/Lower_Princes,America/Marigot,America/Montserrat,America/Port_of_Spain,America/St_Barthelemy,America/St_Kitts,America/St_Lucia,America/St_Thomas,America/St_Vincent,America/Tortola,America/Virgin,PRT|America/Puerto_Rico
        America/Argentina/ComodRivadavia,America/Catamarca|America/Argentina/Catamarca
        America/Atikokan,America/Cayman,America/Coral_Harbour|America/Panama
        America/Atka,US/Aleutian|America/Adak
        America/Cordoba,America/Rosario|America/Argentina/Cordoba
        America/Creston,PNT,US/Arizona|America/Phoenix
        America/Ensenada,America/Santa_Isabel,Mexico/BajaNorte|America/Tijuana
        America/Fort_Wayne,America/Indianapolis,IET,US/East-Indiana|America/Indiana/Indianapolis
        America/Godthab|America/Nuuk
        America/Jujuy|America/Argentina/Jujuy
        America/Knox_IN,US/Indiana-Starke|America/Indiana/Knox
        America/Louisville|America/Kentucky/Louisville
        America/Mendoza|America/Argentina/Mendoza
        America/Montreal,America/Nassau,America/Nipigon,America/Thunder_Bay,Canada/Eastern|America/Toronto
        America/Pangnirtung|America/Iqaluit
        America/Porto_Acre,Brazil/Acre|America/Rio_Branco
        America/Rainy_River,Canada/Central|America/Winnipeg
        America/Shiprock,MST7MDT,Navajo,US/Mountain|America/Denver
        America/Yellowknife,Canada/Mountain|America/Edmonton
        Antarctica/DumontDUrville,Pacific/Chuuk,Pacific/Truk,Pacific/Yap|Pacific/Port_Moresby
        Antarctica/McMurdo,Antarctica/South_Pole,NST,NZ|Pacific/Auckland
        Antarctica/Syowa,Asia/Aden,Asia/Kuwait|Asia/Riyadh
        Arctic/Longyearbyen,Atlantic/Jan_Mayen,Europe/Copenhagen,Europe/Oslo,Europe/Stockholm|Europe/Berlin
        Asia/Ashkhabad|Asia/Ashgabat
        Asia/Bahrain|Asia/Qatar
        Asia/Brunei|Asia/Kuching
        Asia/Calcutta,IST|Asia/Kolkata
        Asia/Choibalsan,Asia/Ulan_Bator|Asia/Ulaanbaatar
        Asia/Chongqing,Asia/Chungking,Asia/Harbin,CTT,PRC|Asia/Shanghai
        Asia/Dacca,BST|Asia/Dhaka
        Asia/Istanbul,Turkey|Europe/Istanbul
        Asia/Kashgar|Asia/Urumqi
        Asia/Katmandu|Asia/Kathmandu
        Asia/Kuala_Lumpur,Singapore|Asia/Singapore
        Asia/Macao|Asia/Macau
        Asia/Muscat,Indian/Mahe,Indian/Reunion|Asia/Dubai
        Asia/Phnom_Penh,Asia/Vientiane,Indian/Christmas|Asia/Bangkok
        Asia/Rangoon,Indian/Cocos|Asia/Yangon
        Asia/Saigon,VST|Asia/Ho_Chi_Minh
        Asia/Tel_Aviv,Israel|Asia/Jerusalem
        Asia/Thimbu|Asia/Thimphu
        Asia/Ujung_Pandang|Asia/Makassar
        Atlantic/Faeroe|Atlantic/Faroe
        Australia/Currie,Australia/Tasmania|Australia/Hobart
        Australia/LHI|Australia/Lord_Howe
        Australia/Queensland|Australia/Brisbane
        Australia/South|Australia/Adelaide
        Australia/Victoria|Australia/Melbourne
        Australia/West|Australia/Perth
        Australia/Yancowinna|Australia/Broken_Hill
        BET,Brazil/East|America/Sao_Paulo
        Brazil/DeNoronha|America/Noronha
        Brazil/West|America/Manaus
        CAT|Africa/Harare
        CET,Europe/Amsterdam,Europe/Luxembourg,MET|Europe/Brussels
        CNT,Canada/Newfoundland|America/St_Johns
        CST,CST6CDT,US/Central|America/Chicago
        Canada/Atlantic|America/Halifax
        Canada/Pacific|America/Vancouver
        Canada/Saskatchewan|America/Regina
        Canada/Yukon|America/Whitehorse
        Chile/Continental|America/Santiago
        Chile/EasterIsland|Pacific/Easter
        Cuba|America/Havana
        EAT|Africa/Addis_Ababa
        ECT,Europe/Monaco|Europe/Paris
        EET|Europe/Athens
        EST5EDT,US/Eastern|America/New_York
        Eire|Europe/Dublin
        Etc/GMT+0,Etc/GMT-0,Etc/GMT0,Etc/Greenwich,GMT,GMT0,Greenwich|Etc/GMT
        Etc/UCT,Etc/Universal,Etc/Zulu,UCT,UTC,Universal,Zulu|Etc/UTC
        Europe/Belfast,Europe/Guernsey,Europe/Isle_of_Man,Europe/Jersey,GB,GB-Eire|Europe/London
        Europe/Bratislava|Europe/Prague
        Europe/Busingen,Europe/Vaduz|Europe/Zurich
        Europe/Kiev,Europe/Uzhgorod,Europe/Zaporozhye|Europe/Kyiv
        Europe/Ljubljana,Europe/Podgorica,Europe/Sarajevo,Europe/Skopje,Europe/Zagreb|Europe/Belgrade
        Europe/Mariehamn|Europe/Helsinki
        Europe/Nicosia|Asia/Nicosia
        Europe/San_Marino,Europe/Vatican|Europe/Rome
        Europe/Tiraspol|Europe/Chisinau
        Hongkong|Asia/Hong_Kong
        Indian/Kerguelen|Indian/Maldives
        Iran|Asia/Tehran
        JST,Japan|Asia/Tokyo
        Jamaica|America/Jamaica
        Kwajalein|Pacific/Kwajalein
        Libya|Africa/Tripoli
        MIT|Pacific/Apia
        Mexico/BajaSur|America/Mazatlan
        Mexico/General|America/Mexico_City
        NET|Asia/Yerevan
        NZ-CHAT|Pacific/Chatham
        PLT|Asia/Karachi
        PST,PST8PDT,US/Pacific|America/Los_Angeles
        Pacific/Enderbury|Pacific/Kanton
        Pacific/Funafuti,Pacific/Majuro,Pacific/Wake,Pacific/Wallis|Pacific/Tarawa
        Pacific/Johnston,US/Hawaii|Pacific/Honolulu
        Pacific/Midway,Pacific/Samoa,US/Samoa|Pacific/Pago_Pago
        Pacific/Pohnpei,Pacific/Ponape,SST|Pacific/Guadalcanal
        Pacific/Saipan|Pacific/Guam
        Poland|Europe/Warsaw
        Portugal,WET|Europe/Lisbon
        ROK|Asia/Seoul
        US/Michigan|America/Detroit
        W-SU|Europe/Moscow
        """;
}
