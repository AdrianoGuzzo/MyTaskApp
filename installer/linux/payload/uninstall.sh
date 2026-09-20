#!/usr/bin/env bash
#
# Remove o MyTaskApp do usuario atual.
#
# Por padrao apaga a aplicacao e os atalhos, e PRESERVA os dados
# (~/.config/MyTaskApp: banco, lembretes, ajustes). Apagar os dados e uma
# escolha explicita: --purge, e ainda assim com confirmacao.

set -euo pipefail

app_name='MyTaskApp'

prefix="${PREFIX:-$HOME/.local}"
install_dir="$prefix/share/$app_name"
bin_link="$prefix/bin/mytaskapp"
desktop_file="$prefix/share/applications/mytaskapp.desktop"
icon_file="$prefix/share/icons/hicolor/256x256/apps/mytaskapp.png"

data_dir="${XDG_CONFIG_HOME:-$HOME/.config}/$app_name"

purge=0
assume_yes=0

for arg in "$@"; do
  case "$arg" in
    --purge) purge=1 ;;
    --yes|-y) assume_yes=1 ;;
    -h|--help)
      cat <<EOF
Uso: uninstall.sh [--purge] [--yes]

  (sem opcoes)  remove a aplicacao e os atalhos, mantem seus dados
  --purge       remove tambem $data_dir (tarefas, lembretes, ajustes)
  --yes         nao pergunta (so tem efeito junto com --purge)
EOF
      exit 0
      ;;
    *)
      echo "Opcao desconhecida: $arg" >&2
      exit 2
      ;;
  esac
done

echo "Removendo a aplicacao..."

rm -rf "$install_dir"
rm -f "$bin_link" "$desktop_file" "$icon_file"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$prefix/share/applications" >/dev/null 2>&1 || true
fi

if [ "$purge" -eq 1 ] && [ -d "$data_dir" ]; then
  # Uma unica remocao de dados no arquivo inteiro, e ela pergunta.
  if [ "$assume_yes" -ne 1 ]; then
    echo
    echo "ATENCAO: isto apaga definitivamente suas tarefas, lembretes e ajustes em:"
    echo "  $data_dir"
    printf 'Apagar? [s/N] '
    read -r answer
    case "$answer" in
      s|S|y|Y) ;;
      *) purge=0; echo 'Cancelado -- seus dados foram mantidos.' ;;
    esac
  fi

  if [ "$purge" -eq 1 ]; then
    rm -rf "$data_dir"
    echo "Dados removidos."
  fi
fi

echo
echo "MyTaskApp removido."

if [ -d "$data_dir" ]; then
  echo "Seus dados continuam em: $data_dir"
  echo "Reinstalar o MyTaskApp retoma exatamente de onde voce parou."
fi
