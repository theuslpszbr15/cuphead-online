# Cuphead Online

Modo online para o Cuphead, por **Eosplash**.

Dois jogadores jogam o co-op local do Cuphead pela internet. O netcode é **lockstep**: os dois PCs rodam o jogo inteiro e trocam apenas os controles de cada frame. Por isso a imagem fica idêntica nos dois lados e o chefe é o mesmo para os dois.

> Este repositório tem **só o código do mod**. Não tem nenhum arquivo nem código do Cuphead.
> O jogo é da Studio MDHR: para jogar, cada um precisa da própria cópia do Cuphead.

## Como jogar

Baixe o `Cuphead-Online-v6.zip` que eu mandei por fora e siga o `LEIA-ME.txt` que vem dentro. Resumindo:

1. Feche o Cuphead e coloque o `Cuphead-Online.exe` na pasta do jogo.
2. Quem hospeda clica em **Hospedar partida** e passa o código da sala.
3. Quem entra cola o código e clica em **Entrar**.
4. Os dois entram num save (no mapa) e a partida começa.

Os dois precisam da mesma versão do mod: versões diferentes não conectam.

## O que tem na v6

- **Lockstep determinístico**: aleatoriedade com seed compartilhada, os acertos de cada frame aplicados numa ordem fixa, e todas as leituras de controle passando pelo netcode.
- **Atraso de controle automático**: se ajusta ao ping durante a partida.
- **Correção automática**: se o chefe dessincronizar, a luta recomeça para os dois.
- **Aviso de versão**: aparece se o mod ou os arquivos do jogo forem diferentes entre os PCs.
- **Progresso compartilhado**: quem entra como amigo ganha uma cópia do progresso do host no **slot 2**, sem mexer no próprio save.
- **Barra de status**: mostra quem é Cuphead e quem é Mugman, a qualidade da conexão e o ping.

## Estrutura

| Pasta | Conteúdo |
|---|---|
| `mod/` | Netcode: `NetcodeSync` (lockstep), `NetSession` (conexão, salas e reconexão), `NetInput` (controles), `NetRandom`, `NetDamageQueue`, `OnlineLobby` (lobby, tecla F1) e o resto |
| `launcher/` | `Cuphead-Online.exe`: instala o mod e abre o jogo como host ou amigo |
| `tools/` | Scripts de teste: duas instâncias locais com bot de input, atraso de rede simulado e comparação dos dois jogos frame a frame (`deepdiff.py`, `dmgdiff.py`) |

`HelperBot.cs` é um bot de P2 para co-op local. Ainda está em desenvolvimento e só entra na compilação com o símbolo `HELPERBOT`.

## Compilar

O mod precisa ser compilado junto com o código do jogo, e o código do jogo **não** está aqui. Para isso você precisa do seu próprio `Assembly-CSharp.dll`, preparado localmente. Um instalador que faz isso sozinho, na cópia do jogo de cada um, está em desenvolvimento.

## Testes

```powershell
# duas instâncias locais na luta dos sapos, com 60 ms + 30 ms de oscilação, comparando os jogos frame a frame
.\tools\nettest.ps1 -Level Frogs -Seconds 150 -GodMode -DeepTrace -LagMs 60 -JitterMs 30
python .\tools\deepdiff.py .\testlogs\run
```
