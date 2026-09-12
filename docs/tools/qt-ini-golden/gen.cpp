// SPDX-License-Identifier: GPL-3.0-only
// Dev-time helper: generates golden INI files with real QSettings (IniFormat) so the C#
// port (Flint.Core/Settings) can be byte-compared against Qt's writer and parser.
// Build (macOS, Homebrew Qt6):
//   clang++ -std=c++17 -fPIC gen.cpp -o qt-ini-golden $(pkg-config --cflags --libs Qt6Core)
#include <QByteArray>
#include <QDateTime>
#include <QList>
#include <QMetaType>
#include <QSettings>
#include <QStringList>
#include <QVariant>
#include <QVariantList>
#include <QVariantMap>

#include <cstdio>

static void writeGolden(const char *path)
{
    QSettings s(QString(path), QSettings::IniFormat);
    s.setFallbacksEnabled(false);
    s.clear();

    // --- strings: quoting decision matrix ---
    s.setValue("str_plain", "system");
    s.setValue("str_space_mid", "Cascadia Code");
    s.setValue("str_leading_space", " padded");
    s.setValue("str_trailing_space", "padded ");
    s.setValue("str_both_space", " padded ");
    s.setValue("str_contains_eq", "AAAA/w==");
    s.setValue("str_contains_comma", "Azul Systems, Inc.");
    s.setValue("str_contains_semi", "a;b");
    s.setValue("str_contains_quotes", "\"value2\" with quotes");
    s.setValue("str_mixed_special", "env mesa=true");
    s.setValue("str_semi_list", "1;2;3;4");
    s.setValue("str_all", "val=\"$INST_JAVA\" -jar; ls ");
    s.setValue("str_hash", "some data#something");
    s.setValue("str_braces", "{}");
    s.setValue("str_slash", "/usr/bin/jvisualvm");
    s.setValue("str_colon", "a:b");
    s.setValue("str_percent", "100%");
    s.setValue("str_empty", "");
    s.setValue("str_unicode", "中文测试★");
    s.setValue("str_ctrl", QString::fromUtf8("a\x01\x02b"));
    s.setValue("str_escapes", "a\nb\t\n\\\\\\C:\\Program files\\terrible\\name\\of something\\#thisIsNotAComment");
    s.setValue("str_json", "{\"a\":true,\"b\":\"x,y=z\"}");
    s.setValue("str_at_start", "@foo");
    s.setValue("str_at_bang", "@Invalid() lookalike");
    s.setValue("str_numberlike", "1.0.0");
    s.setValue("str_true_text", "true");

    // --- numbers / bools ---
    s.setValue("bool_true", true);
    s.setValue("bool_false", false);
    s.setValue("int_pos", 8192);
    s.setValue("int_neg", -42);
    s.setValue("int_zero", 0);
    s.setValue("long_long", (qlonglong)9223372036854775807LL);
    s.setValue("double_plain", 3.25);
    s.setValue("double_small", 0.5);
    s.setValue("uint_val", (uint)4294967295U);

    // --- lists ---
    s.setValue("stringlist_multi", QStringList({"a", "b", "c"}));
    s.setValue("stringlist_empty", QStringList());
    s.setValue("stringlist_single", QStringList({"solo"}));
    s.setValue("stringlist_single_special", QStringList({"solo,x"}));
    s.setValue("stringlist_single_quoted_spaces", QStringList({" solo "}));
    s.setValue("stringlist_needs_quotes", QStringList({"x,y", "a;b", "c=d", "plain", "with space"}));
    s.setValue("stringlist_empty_elem", QStringList({"", "b"}));
    s.setValue("stringlist_trailing_empty", QStringList({"a", ""}));
    s.setValue("variantlist_int", QVariantList({1, 2, 3, 10}));
    s.setValue("variantlist_one", QVariantList{QVariant(42)});
    s.setValue("variantlist_empty", QVariantList());

    // --- byte arrays ---
    s.setValue("byte_array_text", QByteArray("hello world"));
    s.setValue("byte_array_binary", QByteArray("bin\x01\x02\xff\x00data", 10));
    s.setValue("byte_array_empty", QByteArray());

    // --- grouped keys (slash in key names, like Prism's UI/*_Page/Columns) ---
    s.setValue("UI/mods_Page/Columns", QByteArray("AAAA/w=="));
    s.setValue("UI/other", "plain");
    s.setValue("Launcher/some/deep/key", 7);
    s.sync();
    fprintf(stderr, "wrote %s status=%d\n", path, (int)s.status());
}

// Lossless dump of what QSettings reads back from a file: key \t metaType \t repr
static void dumpRead(const char *path, const char *outPath)
{
    QSettings s(QString(path), QSettings::IniFormat);
    s.setFallbacksEnabled(false);
    FILE *out = fopen(outPath, "wb");
    const auto keys = s.allKeys();
    for (const QString &key : keys) {
        QVariant v = s.value(key);
        QString repr;
        switch (v.metaType().id()) {
        case QMetaType::UnknownType: repr = "@INVALID"; break;
        case QMetaType::Bool: repr = v.toBool() ? "BOOL:true" : "BOOL:false"; break;
        case QMetaType::Int: repr = QString("INT:%1").arg(v.toInt()); break;
        case QMetaType::UInt: repr = QString("UINT:%1").arg(v.toUInt()); break;
        case QMetaType::LongLong: repr = QString("LL:%1").arg(v.toLongLong()); break;
        case QMetaType::Double: repr = QString("DBL:%1").arg(v.toDouble(), 0, 'g', 17); break;
        case QMetaType::QString: repr = "STR:" + v.toString(); break;
        case QMetaType::QStringList: repr = "STRLIST:[" + v.toStringList().join(",") + "]"; break;
        case QMetaType::QVariantList: {
            QStringList parts;
            for (const auto &e : v.toList())
                parts << e.toString();
            repr = "VLIST:[" + parts.join(",") + "]";
            break;
        }
        case QMetaType::QByteArray: repr = "BA:[" + v.toByteArray().toBase64() + "]"; break;
        default: repr = "OTHER:" + v.toString(); break;
        }
        // tab/newline-safe encoding: escape tabs and newlines into \t \n markers
        repr.replace("\\", "\\\\").replace("\t", "<TAB>").replace("\n", "<NL>").replace("\r", "<CR>");
        QString k = key;
        k.replace("\\", "\\\\").replace("\t", "<TAB>").replace("\n", "<NL>");
        fprintf(out, "%s\t%s\t%s\n", qUtf8Printable(k), qUtf8Printable(v.typeName() ? v.typeName() : "invalid"), qUtf8Printable(repr));
    }
    fclose(out);
    fprintf(stderr, "dumped %s from %s (%lld keys)\n", outPath, path, (long long)keys.size());
}

static void writeTorture(const char *path)
{
    // Hand-written quirks file; ground truth is whatever QSettings reads back (see dump).
    const char *content =
        "; a leading comment\n"
        "# hash comment\n"
        "[general]\n"
        "unquoted_escapes=hello\\nworld\\ttab\\x41\\101\n"
        "quoted=\"  padded  \"\n"
        "quoted_escapes=\"line1\\nline2 \\\"q\\\" end\"\n"
        "after_quote_junk=\"abc\" trailing\n"
        "empty_quote=\"\"\n"
        "no_newline_at_eof=tail"
        "\n"
        "[%General]\n"
        "escaped_general_key=works\n"
        "[Escaped%20Section]\n"
        "inner%2Fkey=slashy\n"
        "[UI\\Nested]\n"
        "key\\with\\backslashes=deep\n"
        "at_bytearray=@ByteArray(\\x00\\x01\\xff)\n"
        "at_invalid=@Invalid()\n"
        "at_double_at=@@literal\n"
        "spaces_before_eq =   value   \n";
    FILE *f = fopen(path, "wb");
    fwrite(content, 1, strlen(content), f);
    fclose(f);
    fprintf(stderr, "wrote %s\n", path);
}

int main(int argc, char **argv)
{
    if (argc < 3) {
        fprintf(stderr, "usage: %s write <ini> | torture <ini> | dump <ini> <out>\n", argv[0]);
        return 2;
    }
    QString mode = argv[1];
    if (mode == "write")
        writeGolden(argv[2]);
    else if (mode == "torture")
        writeTorture(argv[2]);
    else if (mode == "dump" && argc == 4)
        dumpRead(argv[2], argv[3]);
    else
        return 2;
    return 0;
}
