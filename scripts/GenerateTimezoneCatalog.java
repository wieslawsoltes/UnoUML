import java.io.BufferedWriter;
import java.io.OutputStreamWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.TimeZone;
import java.util.TreeMap;
import java.util.zip.GZIPOutputStream;

/** Regenerates the pinned Java 17 timezone names consumed by the native formatter. */
public final class GenerateTimezoneCatalog {
    public static void main(String[] args) throws Exception {
        if (Runtime.version().feature() != 17)
            throw new IllegalStateException("Use the Java 17 PlantUML reference runtime.");
        Locale.setDefault(Locale.US);
        String[] zones = TimeZone.getAvailableIDs();
        Arrays.sort(zones);
        Map<String, Locale> supported = new TreeMap<>();
        for (Locale locale : Locale.getAvailableLocales()) {
            if (!locale.getLanguage().isEmpty()) supported.put(locale.toLanguageTag(), locale);
        }
        Map<List<String>, Integer> names = new LinkedHashMap<>();
        Map<List<Integer>, Integer> tables = new LinkedHashMap<>();
        Map<String, Integer> locales = new TreeMap<>();
        for (var entry : supported.entrySet()) {
            List<Integer> table = new ArrayList<>(zones.length);
            for (String identifier : zones) {
                TimeZone zone = TimeZone.getTimeZone(identifier);
                Locale locale = entry.getValue();
                List<String> row = List.of(
                    zone.getDisplayName(false, TimeZone.SHORT, locale),
                    zone.getDisplayName(true, TimeZone.SHORT, locale),
                    zone.getDisplayName(false, TimeZone.LONG, locale),
                    zone.getDisplayName(true, TimeZone.LONG, locale));
                table.add(names.computeIfAbsent(row, ignored -> names.size()));
            }
            locales.put(entry.getKey(), tables.computeIfAbsent(List.copyOf(table), ignored -> tables.size()));
        }
        Path destination = Path.of(args.length == 0
            ? "src/UnoUML.PlantUml/Resources/timezones.json.gz" : args[0]);
        try (var output = new BufferedWriter(new OutputStreamWriter(
                new GZIPOutputStream(Files.newOutputStream(destination)), StandardCharsets.UTF_8))) {
            output.write("{\"reference\":");
            string(output, "OpenJDK " + Runtime.version());
            output.write(",\"zones\":");
            strings(output, Arrays.asList(zones));
            output.write(",\"names\":[");
            boolean comma = false;
            for (var row : names.keySet()) {
                if (comma) output.write(',');
                strings(output, row);
                comma = true;
            }
            output.write("],\"tables\":[");
            comma = false;
            for (var table : tables.keySet()) {
                if (comma) output.write(',');
                output.write('[');
                for (int index = 0; index < table.size(); index++) {
                    if (index != 0) output.write(',');
                    output.write(Integer.toString(table.get(index)));
                }
                output.write(']');
                comma = true;
            }
            output.write("],\"locales\":{");
            comma = false;
            for (var entry : locales.entrySet()) {
                if (comma) output.write(',');
                string(output, entry.getKey());
                output.write(':');
                output.write(Integer.toString(entry.getValue()));
                comma = true;
            }
            output.write("}}");
        }
        System.out.printf(Locale.ROOT, "%d locales, %d timezone IDs, %d tables, %d name groups, %d bytes%n",
            locales.size(), zones.length, tables.size(), names.size(), Files.size(destination));
    }

    private static void strings(BufferedWriter output, List<String> values) throws Exception {
        output.write('[');
        for (int index = 0; index < values.size(); index++) {
            if (index != 0) output.write(',');
            string(output, values.get(index));
        }
        output.write(']');
    }

    private static void string(BufferedWriter output, String value) throws Exception {
        output.write('"');
        for (int index = 0; index < value.length(); index++) {
            char character = value.charAt(index);
            if (character == '"' || character == '\\') output.write('\\');
            if (character < 0x20) output.write(String.format(Locale.ROOT, "\\u%04x", (int) character));
            else output.write(character);
        }
        output.write('"');
    }
}
