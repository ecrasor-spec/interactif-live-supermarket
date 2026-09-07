# Interactif Live — Supermarket Simulator

Plugin BepInEx IL2CPP dédié à l'intégration TikTok d'Interactif Live.

## État

Le projet est au stade de bootstrap : le plugin doit d'abord se charger dans le jeu et exposer sa version. Les effets de jeu seront ajoutés après génération des assemblages IL2CPP de Supermarket Simulator, afin de cibler les classes de la version installée sans inventer de noms de méthodes.

## Prérequis

- Supermarket Simulator 1.1 ou supérieur.
- Pack BepInEx IL2CPP adapté au jeu.
- Premier lancement du jeu après installation de BepInEx, puis fermeture au menu principal.
- Assemblages générés dans `BepInEx/interop`.

## Contrat prévu

Le plugin expose maintenant un pont de test limité à `http://127.0.0.1:18946/` : `GET /health` et `POST /action`. Il ne modifie pas encore le gameplay ; il journalise l’action reçue et renvoie `gameplay: false`. Chaque action contiendra ensuite un identifiant, le pseudo TikTok, les paramètres et une date d'expiration. Le plugin devra refuser les actions expirées, limiter leur fréquence et journaliser le résultat dans `BepInEx/LogOutput.log`.
