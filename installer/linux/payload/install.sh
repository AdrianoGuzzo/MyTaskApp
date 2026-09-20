#!/usr/bin/env bash
#
# Instala o MyTaskApp para o usuario atual.
#
# Sem sudo, sem tocar em /usr, sem servico de sistema: tudo mora sob $HOME.
# Uma atualizacao substitui os binarios; ~/.config/MyTaskApp (banco, ajustes,
# logs) nunca e tocada por este script.

set -euo pipefail

app_name='MyTaskApp'
source_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

prefix="${PREFIX:-$HOME/.local}"
install_dir="$prefix/share/$app_name"
bin_dir="$prefix/bin"
desktop_dir="$prefix/share/applications"
icon_dir="$prefix/share/icons/hicolor/256x256/apps"

# Onde o app guarda os dados. Definido por
# Environment.SpecialFolder.ApplicationData -- so aparece aqui para a mensagem
# final e para deixar explicito que NADA abaixo escreve nesta pasta.
data_dir="${XDG_CONFIG_HOME:-$HOME/.config}/$app_name"

if [ -d "$install_dir" ]; then
  echo "Atualizando a instalacao existente em $install_dir"
else
  echo "Instalando em $install_dir"
fi

mkdir -p "$install_dir" "$bin_dir" "$desktop_dir" "$icon_dir"

# Substitui os binarios antigos sem apagar a pasta inteira primeiro: uma queda
# no meio deixa uma instalacao reparavel rodando install.sh de novo.
cp -rf "$source_dir/bin/." "$install_dir/"
chmod +x "$install_dir/MyTaskApp"

ln -sfn "$install_dir/MyTaskApp" "$bin_dir/mytaskapp"

cp -f "$source_dir/mytaskapp.png" "$icon_dir/mytaskapp.png"

# O Exec aponta para o binario real, nao para o symlink: um .desktop que
# depende do PATH quebra quando o lancador nao herda o ambiente do shell.
sed "s|@EXEC@|$install_dir/MyTaskApp|g" "$source_dir/mytaskapp.desktop" \
  > "$desktop_dir/mytaskapp.desktop"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$desktop_dir" >/dev/null 2>&1 || true
fi

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f -t "$prefix/share/icons/hicolor" >/dev/null 2>&1 || true
fi

cat <<EOF

MyTaskApp instalado.

  aplicacao : $install_dir
  comando   : $bin_dir/mytaskapp
  atalho    : $desktop_dir/mytaskapp.desktop
  seus dados: $data_dir  (preservada em atualizacoes e desinstalacao)

Se '$bin_dir' nao estiver no PATH, adicione:
  export PATH="\$PATH:$bin_dir"

Para desinstalar: $install_dir/uninstall.sh
EOF

# O desinstalador acompanha a instalacao: sem isso o usuario precisaria guardar
# o tarball para conseguir remover o app.
cp -f "$source_dir/uninstall.sh" "$install_dir/uninstall.sh"
chmod +x "$install_dir/uninstall.sh"
