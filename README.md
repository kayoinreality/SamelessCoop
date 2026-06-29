# SamelessCoop — Dark Souls II: Seamless Co-op

Launcher **único** para jogar o Seamless Co-op de *Dark Souls II: Scholar of the First Sin*
com **save separado** e **sem nenhum menu dentro do jogo**.

É um wrapper em volta do mod open-source [scheissgeist/Seamless](https://github.com/scheissgeist/Seamless)
(MIT), pensado para ser à prova de erro: você configura tudo num CMD e joga.

## Por que existe

O mod base é ótimo, mas, instalado “na mão”, ele:

- fica **ativo em todo launch** da Steam (some o vanilla);
- grava no **mesmo** `DS2SOFS0000.sl2` do seu save normal;
- depende do **overlay ImGui (tecla INSERT)** dentro do jogo — que o DS2 não curte muito.

O SamelessCoop resolve os três:

| Problema | Solução |
|---|---|
| Mod sempre ativo | O launcher instala o mod só durante a sessão e **remove ao fechar** |
| Save sobrescrito | **Troca de saves**: o vanilla é protegido em `saves/vanilla/` (+ backups datados) e o co-op vive em `saves/seamless/` |
| Config in-game | **Auto-host / auto-join** lendo a config; o **overlay é desligado** (`disable_overlay`) |
| Host vs Joiner | **Um launcher só**; você escolhe no CMD de configuração |
| Pedra correta | O launcher entrega automaticamente a pedra do papel: Host recebe a **Pedra Branca**; Joiner recebe a **Pedra Branca Pequena** |

A conexão usa um **servidor privado** (modelo ds3os) em modo offline — você **não** toca nos
servidores oficiais da FromSoftware durante o co-op.

## Instalação

1. Tenha o *Dark Souls II: Scholar of the First Sin* (Steam) instalado.
2. Rode **`Instalar.bat`**. Ele confere as dependências (.NET + Visual C++ Redistributable),
   compila o launcher e roda um autoteste de segurança do save.
3. Pronto: use o **`SamelessCoop.exe`**.

> Para co-op por internet, host e joiner entram na mesma rede virtual (Hamachi / ZeroTier) e
> usam o IP `25.x.x.x`. Em LAN, IP local. Sem VPN, o host libera a porta UDP **27015**.

## Como jogar

1. Rode `SamelessCoop.exe` → abre a **configuração** (modo Host/Join, IP, senha, dificuldade).
2. Confirma → o jogo abre em `-offline` e a sessão **conecta sozinha**.
3. Dentro do jogo **não há menu**: o Host recebe a Pedra Branca; o Joiner recebe a Pedra Branca Pequena. Usem a pedra recebida para se invocar.
4. Ao fechar, o save vanilla volta intacto e o mod sai da pasta do jogo.

Detalhes completos em [`LEIA-ME.txt`](LEIA-ME.txt).

## Compilar do código

- **Launcher (C#):** `build.bat` (usa o compilador C# embutido no Windows; sem instalar nada).
- **Mod (`.dll`, C++):** `src/build_dll.bat` (precisa do *Visual Studio 2022 Build Tools*, workload C++).
  As modificações de auto-sessão / overlay desligado estão em
  [`src/src/core/mod.cpp`](src/src/core/mod.cpp) e [`src/include/mod.h`](src/include/mod.h).

## Segurança

- O save vanilla nunca é gravado pelo jogo modado; backups datados em `saves/backups/` nunca são apagados.
- Servidor privado + modo offline reduzem muito o risco de ban. Mesmo assim: **não misture com o online oficial**.

## Créditos e licença

- Mod base: **[scheissgeist/Seamless](https://github.com/scheissgeist/Seamless)** — Yui — licença MIT (ver [`src/LICENSE`](src/LICENSE)).
- Técnica de interceptação: [ds3os](https://github.com/TLeonardUK/ds3os), [Dear ImGui](https://github.com/ocornut/imgui), [MinHook](https://github.com/TsudaKagewortu/minhook).

Este projeto é um trabalho derivado distribuído sob a mesma licença **MIT**.
