# Interactif Live — Supermarket Simulator

Plugin BepInEx IL2CPP dédié à l'intégration TikTok d'Interactif Live.

Le dépôt officiel du plugin est : https://github.com/ecrasor-spec/interactif-live-supermarket

## État actuel

Le plugin `0.1.11-dev` est fonctionnel avec Supermarket Simulator 1.0.43.0 et expose un pont local utilisé par Interactif Live. Les actions disponibles sont séparées de Minecraft : clients, voleurs, livraisons aléatoires, déchets, saleté, nettoyage, progression et éclairage.

## Prérequis

- Supermarket Simulator 1.1 ou supérieur.
- Pack BepInEx IL2CPP adapté au jeu.
- Premier lancement du jeu après installation de BepInEx, puis fermeture au menu principal.
- Assemblages générés dans `BepInEx/interop`.

## Installation automatique depuis Interactif Live

Dans la carte **Supermarket Simulator**, cliquer sur **Installer / mettre à jour BepInEx** :

1. l’application vérifie que le jeu est fermé ;
2. elle télécharge le dernier pack BepInEx compatible ;
3. elle crée une sauvegarde locale des fichiers BepInEx existants ;
4. elle lit `manifest.json` depuis ce dépôt ;
5. elle télécharge `releases/InteractifLive.Supermarket.dll` ;
6. elle vérifie le SHA-256 ;
7. elle installe la DLL dans `BepInEx/plugins`.

Le téléchargement du plugin est limité aux URLs `raw.githubusercontent.com/ecrasor-spec/interactif-live-supermarket/main/` et l’installation est annulée si le hash ne correspond pas.

## Contrat du pont

Le plugin expose un pont local limité à `http://127.0.0.1:18946/` : `GET /health` et `POST /action`. Les actions sont mises en file et exécutées sur le thread principal Unity. Les livraisons utilisent uniquement les produits débloqués par le joueur et choisissent un produit aléatoire par livraison.
