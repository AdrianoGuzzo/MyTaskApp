#!/usr/bin/env bash
# Confere o APK do app do celular (ADR-067) antes de ele sair do CI.
#
#   check-android-apk.sh <apk>           o APK leva o SQLite nativo (CI dos PRs)
#   check-android-apk.sh <apk> <versão>  e também a assinatura e a versão (release)
#
# Um só script para os dois workflows: a regra do SQLite não pode divergir
# entre o que o PR confere e o que a release publica.
set -euo pipefail

apk="${1:?uso: check-android-apk.sh <apk> [<versão>]}"
version="${2:-}"

fail() {
  echo "::error::$1"
  exit 1
}

test -f "$apk" || fail "APK ausente: $apk"

# Sem o libe_sqlite3.so, o app abre e morre na primeira consulta.
# Sem -q: o grep -q sai no primeiro acerto, o unzip leva SIGPIPE (141) e o
# pipefail transforma isso em falha.
unzip -l "$apk" | grep "lib/arm64-v8a/libe_sqlite3.so" >/dev/null \
  || fail "O APK não leva o SQLite nativo (lib/arm64-v8a/libe_sqlite3.so)."

if [ -z "$version" ]; then
  echo "ok: $apk"
  exit 0
fi

# Daqui para baixo, só o APK da release.
sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"
test -n "$sdk" || fail "ANDROID_HOME não está definido; sem ele não há apksigner nem aapt2."
build_tools="$(find "$sdk/build-tools" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | sort -V | tail -1)"
test -n "$build_tools" || fail "Nenhum build-tools em $sdk."

# Assinado com a chave de debug, o APK instala, mas a release seguinte (com a
# chave certa) não atualiza por cima: o Android exige a mesma assinatura, e
# desinstalar apaga os dados do celular.
certs="$("$build_tools/apksigner" verify --print-certs "$apk")" \
  || fail "apksigner não aceitou a assinatura de $apk."
grep "certificate DN:" <<<"$certs" >/dev/null || fail "O APK não tem certificado de assinatura."
if grep "CN=Android Debug" <<<"$certs" >/dev/null; then
  fail "O APK foi assinado com a chave de debug do Android, não com a keystore da release."
fi

# A mesma conta do MyTaskApp.Mobile.csproj: 1.17.0 → 11700. Um versionCode
# que não sobe faz o Android recusar a atualização.
IFS=. read -r major minor patch <<<"$version"
expected_code=$((major * 10000 + minor * 100 + patch))
badging="$("$build_tools/aapt2" dump badging "$apk" | grep "^package:")" \
  || fail "aapt2 não leu o manifesto de $apk."
grep "name='io.github.adrianoguzzo.mytaskapp'" <<<"$badging" >/dev/null \
  || fail "Pacote inesperado: $badging"
grep "versionCode='$expected_code'" <<<"$badging" >/dev/null \
  || fail "versionCode errado (esperado $expected_code para $version): $badging"
grep "versionName='$version'" <<<"$badging" >/dev/null \
  || fail "versionName errado (esperado $version): $badging"

echo "ok: $apk ($version, versionCode $expected_code)"
grep "certificate DN:" <<<"$certs"
