#!/usr/bin/env bash
#
# Gera o pacote Linux do MyTaskApp: um tarball com os binarios, o instalador e
# o desinstalador.
#
# Mesma politica do Windows (ADR-018): binarios em um lugar, dados do usuario
# em outro. Aqui os dados caem em ~/.config/MyTaskApp porque e para la que
# Environment.SpecialFolder.ApplicationData aponta no Linux -- nenhuma linha de
# codigo especifica de sistema operacional foi necessaria.
#
# Uso:
#   installer/linux/build.sh [runtime]     # padrao: linux-x64

set -euo pipefail

runtime="${1:-linux-x64}"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"

desktop_project="$repo_root/src/MyTaskApp.Desktop/MyTaskApp.Desktop.csproj"
publish_dir="$repo_root/artifacts/publish/$runtime"
stage_dir="$repo_root/artifacts/stage/$runtime"
output_dir="$repo_root/artifacts/installer"

step() { printf '\n==> %s\n' "$1"; }

step 'Lendo a versao do produto'

# A mesma fonte unica do Windows: Directory.Build.props, via MSBuild.
version="$(dotnet msbuild "$desktop_project" -getProperty:Version -v:q --nologo | tr -d '[:space:]')"

if [ -z "$version" ]; then
  echo 'Nao consegui ler a propriedade Version do projeto.' >&2
  exit 1
fi

echo "    versao: $version"

step "Publicando $runtime (self-contained)"

rm -rf "$publish_dir"

# Sem trimming: EF Core e Avalonia dependem de reflexao. InvariantGlobalization
# continua false para os ids IANA de fuso resolverem (ADR-002).
dotnet publish "$desktop_project" \
  -c Release \
  -r "$runtime" \
  --self-contained true \
  -p:PublishTrimmed=false \
  -p:PublishSingleFile=false \
  -p:DebugType=none \
  -o "$publish_dir" \
  --nologo

if [ ! -f "$publish_dir/MyTaskApp" ]; then
  echo "Publish nao produziu $publish_dir/MyTaskApp." >&2
  exit 1
fi

# appsettings.json e carregado com optional:false -- sem ele o app nao inicia.
if [ ! -f "$publish_dir/appsettings.json" ]; then
  echo "Publish nao produziu appsettings.json (o app nao inicia sem ele)." >&2
  exit 1
fi

step 'Montando o pacote'

rm -rf "$stage_dir"
mkdir -p "$stage_dir/bin"

cp -r "$publish_dir/." "$stage_dir/bin/"
chmod +x "$stage_dir/bin/MyTaskApp"

cp "$script_dir/payload/install.sh" "$stage_dir/install.sh"
cp "$script_dir/payload/uninstall.sh" "$stage_dir/uninstall.sh"
chmod +x "$stage_dir/install.sh" "$stage_dir/uninstall.sh"

cp "$script_dir/assets/mytaskapp.png" "$stage_dir/mytaskapp.png"

# A versao entra no .desktop a partir do template, sem ser digitada de novo.
sed "s/@VERSION@/$version/g" "$script_dir/mytaskapp.desktop.in" > "$stage_dir/mytaskapp.desktop"

mkdir -p "$output_dir"

tarball="$output_dir/MyTaskApp-$version-$runtime.tar.gz"
rm -f "$tarball"

tar -czf "$tarball" -C "$(dirname "$stage_dir")" "$(basename "$stage_dir")" \
  --transform "s|^$(basename "$stage_dir")|MyTaskApp-$version|"

printf '\nPacote pronto: %s\n' "$tarball"
printf '\nPara instalar:\n'
printf '    tar -xzf MyTaskApp-%s-%s.tar.gz\n' "$version" "$runtime"
printf '    cd MyTaskApp-%s && ./install.sh\n' "$version"
