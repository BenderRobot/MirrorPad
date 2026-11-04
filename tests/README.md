# Déploiement iPad et migration Debian

Depuis la racine : `python -m unittest discover -s tests -v`.

Les tests de déploiement utilisent des réponses SSH simulées et un shell réel avec
des fonctions inoffensives à la place de dpkg et uicache. Ils vérifient la remontée
d'erreur, la version installée et le report d'un appareil indisponible avant
installation. Aucun appareil n'est contacté.

Le test Debian nécessite `sh` et `dpkg-deb` : il construit un paquet dans un dossier
temporaire, vérifie l'absence des anciennes unités système, puis exécute le script
de migration sur un hôte systemd simulé. Il est ignoré quand ces outils ne sont pas
disponibles et s'exécute dans GitHub Actions sous Ubuntu avant le job iPad.
