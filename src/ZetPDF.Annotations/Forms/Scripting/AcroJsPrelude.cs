namespace ZetPDF.Annotations.Forms.Scripting;

/// <summary>
/// Minimalne API Acrobat JavaScript dla skryptów pól, napisane w samym JavaScripcie (bez obiektów CLR): obiekt
/// <c>event</c>, <c>this.getField(n).value</c>, <c>app.alert</c> (tylko zapis komunikatu) i najczęstsze funkcje AForm:
/// AFSimple_Calculate, AFNumber_Format/Keystroke, AFPercent_Format/Keystroke, AFDate_FormatEx/KeystrokeEx,
/// AFDate_Format/Keystroke, AFRange_Validate, AFSpecial_Format/Keystroke. Funkcje hosta: __get, __set, __has, __all, __alert.
/// </summary>
internal static class AcroJsPrelude
{
    /// <summary>Maks. długość łańcucha tworzonego przez repeat/padStart/padEnd/join i wartości pola (znaki).</summary>
    public const int MaxStringLength = 1024 * 1024;

    /// <summary>Maks. liczba elementów tablicy dla join/fill (zgodnie z Constraints.MaxArraySize).</summary>
    public const int MaxArrayLength = 100_000;

    public const string Source = """
(function () {
  // Ograniczenia pojedynczych dużych alokacji (limit pamięci Jint jest sprawdzany dopiero między instrukcjami).
  // Funkcje wbudowane przechwycone tutaj – skrypt nie może ich podmienić (np. Math.floor), a argumenty są
  // zamieniane na liczby/łańcuchy RAZ (valueOf/toString/getter length nie mogą zwrócić innej wartości przy drugim odczycie).
  var MAXS = 1048576, MAXA = 100000;
  var floor = Math.floor, min = Math.min, num = Number, str = String, apply = Function.prototype.apply;
  var rep = String.prototype.repeat, ps = String.prototype.padStart, pe = String.prototype.padEnd;
  var sconcat = String.prototype.concat, srepl = String.prototype.replace, sreplAll = String.prototype.replaceAll;
  var jn = Array.prototype.join, fl = Array.prototype.fill, push = Array.prototype.push;
  var jsonStr = JSON.stringify, isArr = Array.isArray, keys = Object.keys;
  function lim(n) { if (!(n <= MAXS)) throw new RangeError("ZetPDF limit: string too long"); }
  function limA(n) { if (!(n <= MAXA)) throw new RangeError("ZetPDF limit: array too long"); }
  function int(v) { var n = floor(num(v)); return n === n ? n : 0; }
  function def(o, k, f) { Object.defineProperty(o, k, { value: f, writable: false, configurable: false, enumerable: false }); }
  function strs(a, len) { var out = new Array(len), t = 0; for (var i = 0; i < len; i++) { var e = a[i]; out[i] = e === null || e === undefined ? "" : str(e); t += out[i].length; lim(t); } return out; }
  def(String.prototype, "repeat", function (c) { var s = str(this), n = int(c); if (n > 0) lim(s.length * n); return rep.call(s, n); });
  def(String.prototype, "padStart", function (n, f) { var k = int(n); lim(k); return ps.call(str(this), k, f === undefined ? undefined : str(f)); });
  def(String.prototype, "padEnd", function (n, f) { var k = int(n); lim(k); return pe.call(str(this), k, f === undefined ? undefined : str(f)); });
  def(String.prototype, "concat", function () {
    var s = str(this), len = arguments.length; limA(len);
    var parts = strs(arguments, len), t = s.length;
    for (var i = 0; i < len; i++) { t += parts[i].length; lim(t); }
    return apply.call(sconcat, s, parts);
  });
  function safeReplace(orig, all) {
    return function (pattern, replacement) {
      var s = str(this);
      // wyrażenie regularne (albo obiekt z własnym Symbol.replace) – liczba trafień do s.length + 1 niezależnie od flagi
      // (właściwość global może być getterem zwracającym co innego przy drugim odczycie)
      var matches = !all && typeof pattern === "string" ? 1 : s.length + 1;
      if (typeof replacement === "function") {
        var f = replacement, total = s.length;
        return orig.call(s, pattern, function () { var r = str(apply.call(f, undefined, arguments)); total += r.length; lim(total); return r; });
      }
      var r = str(replacement), dollars = 0;
      for (var i = 0; i < r.length; i++) if (r.charCodeAt(i) === 36) dollars++;
      // górne oszacowanie: każde trafienie zastąpione tekstem r, w którym każde $… może wstawić co najwyżej cały łańcuch
      lim(s.length + matches * (r.length + dollars * s.length));
      return orig.call(s, pattern, r);
    };
  }
  def(String.prototype, "replace", safeReplace(srepl, false));
  if (sreplAll) def(String.prototype, "replaceAll", safeReplace(sreplAll, true));
  def(Array.prototype, "join", function (sep) {
    var len = this.length >>> 0;
    limA(len);
    var sp = sep === undefined ? "," : str(sep);
    lim(len > 0 ? (len - 1) * sp.length : 0);
    var parts = strs(this, len), t = len > 0 ? (len - 1) * sp.length : 0;
    for (var i = 0; i < len; i++) { t += parts[i].length; lim(t); }
    return jn.call(parts, sp);
  });
  def(Array.prototype, "toLocaleString", function () {
    var len = this.length >>> 0; limA(len);
    var parts = new Array(len), t = 0;
    for (var i = 0; i < len; i++) { var e = this[i]; parts[i] = e === null || e === undefined ? "" : str(e.toLocaleString()); t += parts[i].length + 1; lim(t); }
    return jn.call(parts, ",");
  });
  def(Array.prototype, "fill", function () { limA(this.length >>> 0); return apply.call(fl, this, arguments); });
  // JSON.stringify: najpierw kopia z samych wartości prostych (toJSON, gettery i replacer wywołane raz) z oszacowaniem
  // rozmiaru wyniku, dopiero potem oryginał na kopii
  def(JSON, "stringify", function (value, replacer, space) {
    var gap = typeof space === "number" ? min(10, int(space)) : (typeof space === "string" ? space.substring(0, 10).length : 0);
    var allow = null, fn = typeof replacer === "function" ? replacer : null;
    if (isArr(replacer)) { allow = {}; for (var i = 0; i < replacer.length; i++) allow["$" + str(replacer[i])] = true; }
    var total = 0, nodes = 0;
    function add(n) { total += n; lim(total); }
    function walk(holder, key, v, depth) {
      if (++nodes > MAXA) throw new RangeError("ZetPDF limit: object too large");
      if (v !== null && typeof v === "object" && typeof v.toJSON === "function") v = v.toJSON(key);
      if (fn) v = fn.call(holder, key, v);
      if (v === null) { add(4); return null; }
      var t = typeof v;
      if (t === "string") { add(v.length * 6 + 2); return v; }
      if (t === "number" || t === "boolean") { add(25); return v; }
      if (t === "bigint" || t === "undefined" || t === "function" || t === "symbol") { if (t === "bigint") throw new TypeError("BigInt"); return undefined; }
      if (v instanceof Number || v instanceof Boolean) { add(25); return v.valueOf(); }
      if (v instanceof String) { var sv = str(v); add(sv.length * 6 + 2); return sv; }
      add(2 + gap * depth);
      if (isArr(v)) {
        var len = v.length >>> 0; limA(len);
        var out = [];
        for (var j = 0; j < len; j++) { var w = walk(v, str(j), v[j], depth + 1); push.call(out, w === undefined ? null : w); add(2 + gap * (depth + 1)); }
        return out;
      }
      var o = {}, ks = keys(v);
      for (var k = 0; k < ks.length; k++) {
        if (allow && !allow["$" + ks[k]]) continue;
        var x = walk(v, ks[k], v[ks[k]], depth + 1);
        if (x !== undefined) { o[ks[k]] = x; add(ks[k].length * 6 + 4 + gap * (depth + 1)); }
      }
      return o;
    }
    var snap = walk({ "": value }, "", value, 0);
    return snap === undefined ? undefined : jsonStr(snap, null, gap > 0 ? (typeof space === "string" ? space.substring(0, 10) : gap) : undefined);
  });
  // bufory binarne i tablice typowane nie są potrzebne skryptom pól, a omijają limit rozmiaru tablic
  var bin = ["ArrayBuffer", "SharedArrayBuffer", "DataView", "Atomics", "Int8Array", "Uint8Array", "Uint8ClampedArray", "Int16Array", "Uint16Array",
    "Int32Array", "Uint32Array", "Float16Array", "Float32Array", "Float64Array", "BigInt64Array", "BigUint64Array", "WeakRef", "FinalizationRegistry"];
  for (var b = 0; b < bin.length; b++) { try { delete globalThis[bin[b]]; } catch (e) { } }
})();
function __str(v) { var s = v === null || v === undefined ? "" : String(v); if (s.length > 1048576) throw new RangeError("ZetPDF limit: value too long"); return s; }
var event = { value: "", rc: true, willCommit: true, change: "", changeEx: "", name: "", type: "Field", target: null, targetName: "", selStart: 0, selEnd: 0, modifier: false, shift: false };
function __val(s) {
  if (typeof s !== "string") return s;
  if (/^\s*[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?\s*$/.test(s)) return Number(s);
  return s;
}
function Field(n) { this.name = n; }
Object.defineProperty(Field.prototype, "value", {
  get: function () { return __val(__get(this.name)); },
  set: function (v) { __set(this.name, v === null || v === undefined ? "" : String(v)); }
});
Object.defineProperty(Field.prototype, "valueAsString", { get: function () { return __get(this.name); } });
Field.prototype.getArray = function () {
  var all = __all().split("\n"), out = [], p = this.name + ".";
  for (var i = 0; i < all.length; i++) if (all[i].indexOf(p) === 0) out.push(new Field(all[i]));
  return out.length ? out : [this];
};
var __doc = {
  getField: function (n) {
    n = String(n);
    if (__has(n)) return new Field(n);
    var all = __all().split("\n");
    for (var i = 0; i < all.length; i++) if (all[i].indexOf(n + ".") === 0) return new Field(n);
    return null;
  },
  calculateNow: function () { },
  resetForm: function () { }
};
this.getField = __doc.getField;
this.calculateNow = __doc.calculateNow;
var app = { alert: function (m) { __alert(String(m)); return 1; }, beep: function () { }, viewerType: "Reader", platform: "WIN", language: "ENU" };
var console = { println: function (m) { __alert(String(m)); }, show: function () { }, clear: function () { } };

function AFMakeNumber(s) {
  if (typeof s === "number") return s;
  if (s === null || s === undefined) return null;
  s = String(s).replace(/[\s  ]/g, "");
  if (s === "") return null;
  var neg = /^\(.*\)$/.test(s);
  if (neg) s = s.substring(1, s.length - 1);
  s = s.replace(/[^\d.,\-+eE]/g, "");
  if (s.indexOf(",") >= 0 && s.indexOf(".") >= 0) {
    if (s.lastIndexOf(",") > s.lastIndexOf(".")) s = s.replace(/\./g, "").replace(",", ".");
    else s = s.replace(/,/g, "");
  } else if (s.indexOf(",") >= 0) s = s.replace(",", ".");
  if (!/^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$/.test(s)) return null;
  var n = Number(s);
  return isNaN(n) ? null : (neg ? -n : n);
}
function __names(list) {
  if (typeof list === "string") list = list.split(/\s*,\s*/);
  var all = __all().split("\n"), out = [];
  for (var i = 0; i < list.length; i++) {
    var n = String(list[i]);
    if (__has(n)) { out.push(n); continue; }
    for (var j = 0; j < all.length; j++) if (all[j].indexOf(n + ".") === 0) out.push(all[j]);
  }
  return out;
}
function AFSimple_Calculate(op, list) {
  var names = __names(list), res = op === "PRD" ? 1 : 0, count = 0;
  for (var i = 0; i < names.length; i++) {
    var v = AFMakeNumber(__get(names[i]));
    if (v === null) v = 0;
    if (op === "SUM" || op === "AVG") res += v;
    else if (op === "PRD") res *= v;
    else if (op === "MIN") res = count === 0 ? v : Math.min(res, v);
    else if (op === "MAX") res = count === 0 ? v : Math.max(res, v);
    count++;
  }
  if (op === "AVG" && count > 0) res = res / count;
  event.value = res;
}
function __fmtNum(n, dec, sep) {
  dec = Math.max(0, Math.min(10, dec | 0));
  var s = Math.abs(n).toFixed(dec), parts = s.split("."), ip = parts[0], fp = parts.length > 1 ? parts[1] : "";
  var th = [",", "", ".", "", "'"][sep] || "", dp = (sep === 2 || sep === 3) ? "," : ".";
  if (th) ip = ip.replace(/\B(?=(\d{3})+(?!\d))/g, th);
  return ip + (dec > 0 ? dp + fp : "");
}
function AFNumber_Format(nDec, sepStyle, negStyle, currStyle, strCurrency, bCurrencyPrepend) {
  var v = AFMakeNumber(event.value);
  if (v === null) { event.value = ""; return; }
  var s = __fmtNum(v, nDec, sepStyle), cur = strCurrency ? String(strCurrency) : "";
  if (cur) s = bCurrencyPrepend ? cur + s : s + cur;
  if (v < 0 && Number(Math.abs(v).toFixed(Math.max(0, nDec | 0))) !== 0) s = (negStyle === 2 || negStyle === 3) ? "(" + s + ")" : "-" + s;
  event.value = s;
}
function AFNumber_Keystroke(nDec, sepStyle) {
  if (event.willCommit) {
    var s = String(event.value).replace(/^\s+|\s+$/g, "");
    if (s !== "" && AFMakeNumber(s) === null) { event.rc = false; __alert("@Annot.Script.Alert.Number"); }
  } else if (!/^[\d\s.,\-+()]*$/.test(String(event.change))) event.rc = false;
}
function AFPercent_Format(nDec, sepStyle) {
  var v = AFMakeNumber(event.value);
  if (v === null) { event.value = ""; return; }
  event.value = (v < 0 ? "-" : "") + __fmtNum(v * 100, nDec, sepStyle) + "%";
}
function AFPercent_Keystroke(nDec, sepStyle) { AFNumber_Keystroke(nDec, sepStyle); }
function AFRange_Validate(bGreaterThan, nGreaterThan, bLessThan, nLessThan) {
  var v = AFMakeNumber(event.value);
  if (v === null) return;
  if ((bGreaterThan && v < nGreaterThan) || (bLessThan && v > nLessThan)) { event.rc = false; __alert("@Annot.Script.Alert.Range"); }
}
var __months = ["january","february","march","april","may","june","july","august","september","october","november","december"];
function __pad(n, w) { n = String(n); while (n.length < w) n = "0" + n; return n; }
function __fmtDate(d, fmt) {
  return String(fmt).replace(/yyyy|yy|mmmm|mmm|mm|m|dd|d|HH|H|hh|h|MM|M|ss|s|tt/g, function (t) {
    var h12 = d.getHours() % 12 === 0 ? 12 : d.getHours() % 12;
    switch (t) {
      case "yyyy": return __pad(d.getFullYear(), 4);
      case "yy": return __pad(d.getFullYear() % 100, 2);
      case "mmmm": var mn = __months[d.getMonth()]; return mn.charAt(0).toUpperCase() + mn.substring(1);
      case "mmm": var ms = __months[d.getMonth()].substring(0, 3); return ms.charAt(0).toUpperCase() + ms.substring(1);
      case "mm": return __pad(d.getMonth() + 1, 2);
      case "m": return String(d.getMonth() + 1);
      case "dd": return __pad(d.getDate(), 2);
      case "d": return String(d.getDate());
      case "HH": return __pad(d.getHours(), 2);
      case "H": return String(d.getHours());
      case "hh": return __pad(h12, 2);
      case "h": return String(h12);
      case "MM": return __pad(d.getMinutes(), 2);
      case "M": return String(d.getMinutes());
      case "ss": return __pad(d.getSeconds(), 2);
      case "s": return String(d.getSeconds());
      case "tt": return d.getHours() < 12 ? "am" : "pm";
    }
    return t;
  });
}
function __mkDate(y, mo, d, h, mi, s) {
  if (y < 100) y += y < 50 ? 2000 : 1900;
  if (mo < 0 || mo > 11 || d < 1 || d > 31 || h > 23 || mi > 59 || s > 59) return null;
  var r = new Date(y, mo, d, h, mi, s);
  return r.getMonth() === mo && r.getDate() === d ? r : null;
}
function __parseDate(str, fmt) {
  str = String(str).replace(/^\s+|\s+$/g, "");
  if (str === "") return null;
  var iso = /^(\d{4})-(\d{1,2})-(\d{1,2})(?:[T ](\d{1,2}):(\d{2})(?::(\d{2}))?)?$/.exec(str);
  if (iso) return __mkDate(+iso[1], +iso[2] - 1, +iso[3], +(iso[4] || 0), +(iso[5] || 0), +(iso[6] || 0));
  var tokens = String(fmt).match(/yyyy|yy|mmmm|mmm|mm|m|dd|d|HH|H|hh|h|MM|M|ss|s|tt/g) || [];
  var parts = str.match(/\d+|[A-Za-z]+/g) || [];
  var y = new Date().getFullYear(), mo = 0, d = 1, h = 0, mi = 0, s = 0, pm = null, k = 0;
  for (var i = 0; i < tokens.length && k < parts.length; i++) {
    var t = tokens[i], p = parts[k];
    if (t === "tt") { if (/^[ap]/i.test(p)) { pm = /^p/i.test(p); k++; } continue; }
    if (t === "mmm" || t === "mmmm") {
      var idx = -1;
      for (var j = 0; j < 12; j++) if (__months[j].indexOf(p.toLowerCase().substring(0, 3)) === 0) idx = j;
      if (idx < 0) return null;
      mo = idx; k++; continue;
    }
    if (!/^\d+$/.test(p)) return null;
    var n = +p; k++;
    if (t === "yyyy" || t === "yy") y = n;
    else if (t === "mm" || t === "m") mo = n - 1;
    else if (t === "dd" || t === "d") d = n;
    else if (t === "HH" || t === "H" || t === "hh" || t === "h") h = n;
    else if (t === "MM" || t === "M") mi = n;
    else if (t === "ss" || t === "s") s = n;
  }
  if (k < parts.length && tokens.length > 0) return null;
  if (pm === true && h < 12) h += 12;
  if (pm === false && h === 12) h = 0;
  return __mkDate(y, mo, d, h, mi, s);
}
function AFDate_FormatEx(fmt) {
  var s = String(event.value);
  if (s === "") return;
  var d = __parseDate(s, fmt);
  if (d) event.value = __fmtDate(d, fmt);
}
function AFDate_KeystrokeEx(fmt) {
  if (!event.willCommit) return;
  var s = String(event.value);
  if (s !== "" && !__parseDate(s, fmt)) { event.rc = false; __alert("@Annot.Script.Alert.Date"); }
}
var __dateFormats = ["m/d", "m/d/yy", "mm/dd/yy", "mm/yy", "d-mmm", "d-mmm-yy", "dd-mmm-yy", "yy-mm-dd", "mmm-yy", "mmmm-yy", "mmm d, yyyy", "mmmm d, yyyy", "m/d/yy h:MM tt", "m/d/yy HH:MM"];
function AFDate_Format(n) { AFDate_FormatEx(__dateFormats[n] || "m/d/yy"); }
function AFDate_Keystroke(n) { AFDate_KeystrokeEx(__dateFormats[n] || "m/d/yy"); }
function AFSpecial_Format(psf) {
  var s = String(event.value).replace(/\D/g, "");
  if (s === "") return;
  if (psf === 0) event.value = s.substring(0, 5);
  else if (psf === 1) event.value = s.substring(0, 5) + (s.length > 5 ? "-" + s.substring(5, 9) : "");
  else if (psf === 2) event.value = s.length > 7 ? "(" + s.substring(0, 3) + ") " + s.substring(3, 6) + "-" + s.substring(6, 10) : s.substring(0, 3) + "-" + s.substring(3, 7);
  else if (psf === 3) event.value = s.substring(0, 3) + "-" + s.substring(3, 5) + "-" + s.substring(5, 9);
}
function AFSpecial_Keystroke(psf) {
  if (!event.willCommit) { if (!/^[\d\s()\-.]*$/.test(String(event.change))) event.rc = false; return; }
  var n = String(event.value).replace(/\D/g, "").length;
  var ok = psf === 0 ? n === 5 : psf === 1 ? n === 9 : psf === 2 ? (n === 7 || n === 10) : n === 9;
  if (String(event.value) !== "" && !ok) { event.rc = false; __alert("@Annot.Script.Alert.Format"); }
}
""";
}
