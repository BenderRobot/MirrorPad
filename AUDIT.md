# Audit de reprise — 4 novembre 2026

Référence : `main`, commit `f765dab95023ca77b85d0807569ded5b742b2bd8`.
Audit du code et de la chaîne de livraison, sans modification du fonctionnement ni déploiement.

## Vue d'ensemble

Application compagnon Windows (.NET Framework 4.8 / WinForms), Debian (Python / GTK / Mutter / GStreamer) et iPad jailbreaké sous iOS 12 (Objective-C). Protocole binaire commun sur TLS ; découverte réseau et USB côté Windows. Compilation et publication automatisées à chaque push sur main ; compilation iPad sur runner personnel et installation sur l'appareil pendant le workflow.

Points utiles pour la reprise : séparation des composants Windows, documentation détaillée, certificat épinglé pour les PC connus, jetons aléatoires et comparaison constante côté PC. Les fichiers iPad (3 050 lignes) et Debian (1 684 lignes) concentrent beaucoup de responsabilités. Le dépôt ne contient pas de suite automatisée de non-régression du protocole ; le script tactile Windows est un diagnostic manuel.

## Constats prioritaires

### 1. Critique — authentification iPad contournable par un OK prématuré

**Correction locale préparée le 4 novembre 2026 :** transitions explicites partagées
avec des tests C, vérification de la forme des réponses et des jetons, refus des
messages inattendus avant authentification. Les tests sont exécutés avant le job
iPad de publication. Compilation iOS et recette sur appareil encore à effectuer ;
aucun déploiement déclenché par cette correction locale.

Source : `ipad/bSideDesk.m:372–398` et `492–514`.

Dans l'état non authentifié, la branche qui reçoit `OK` ne vérifie ni qu'un HELLO a été reçu, ni qu'un certificat connu a été validé, ni qu'une réponse AUTH est attendue. Un interlocuteur réseau jouant le serveur TLS peut envoyer directement une trame de contrôle OK : `_auth` devient YES et la liaison peut devenir active si aucun PC n'est actif. Le certificat est extrait pendant TLS mais son épinglage n'a lieu que dans la branche HELLO pour un identifiant connu. Les commandes et transferts sont ensuite autorisés pour la liaison active.

Autre conséquence de la même absence de machine à états : après HELLO d'un nouveau PC, recevoir OK seul entre dans la branche `pairing` et accède à `p[1]`, absent ; exception Objective-C possible.

Correction : états explicites pour HELLO, certificat connu/AUTH, nouvel appairage et réponse finale. N'accepter OK qu'après AUTH vers un PC épinglé ; n'accepter PAIRED qu'après une demande d'appairage valide. Fermer sur toute transition inattendue. Tester les messages hors ordre et les trames malformées.

Constat établi par lecture du chemin d'exécution ; exploitation réseau non exécutée sur l'iPad.

### 2. Élevée — une mise à jour Windows peut laisser l'application sans exécutable

**Correction locale préparée le 4 novembre 2026 :** tous les fichiers sont préparés
avant modification ; les originaux sont conservés puis restaurés en cas d'erreur.
L'exécutable est remplacé après les fichiers annexes et le marqueur de succès fait
partie de la même opération. Les recherches concurrentes sont bloquées. Tests
compilés et exécutés sous Windows : installation normale, échec après remplacement
de l'exécutable, exécutable verrouillé, archive incomplète. Tests ajoutés au job
Windows de publication. Cette restauration traite les exceptions d'installation ;
elle ne fournit pas de reprise après arrêt brutal ou coupure de courant.

Source : `windows/src/App.cs:840–853`.

L'exécutable courant est renommé en `.old` avant la copie de tous les nouveaux fichiers. Si une copie échoue avant la copie du nouvel exécutable (espace disque, verrouillage, antivirus), le catch écrit seulement dans le journal. L'application en cours peut continuer mais le prochain lancement échoue ; aucun rétablissement automatique de l'ancien exécutable n'est prévu.

Correction : préparer et valider tous les fichiers, appliquer avec un mécanisme de retour arrière, et restaurer `.old` sur tout échec. Tester une erreur de copie injectée après le renommage.

### 3. Moyenne — désactiver les mises à jour Debian ne désactive pas le service système

**Correction locale préparée le 4 novembre 2026 :** retrait des unités système du
paquet, arrêt et désactivation de l'ancienne minuterie à la mise à niveau. Seule
l'application lance désormais les mises à jour automatiques selon son réglage.
Le service en cours n'est pas tué pour ne pas interrompre une installation.
Test de construction et de migration du paquet ajouté à GitHub Actions.

Sources : `linux/bsidedesk.py:1320,1534`, `linux/build-deb.sh:48`, `linux/bsidedesk-update.timer:5–6`.

Le réglage auto_update arrête les recherches de l'application. Le paquet active aussi une minuterie système au démarrage puis toutes les 30 minutes ; le helper ne lit pas ce réglage et installe la dernière version indépendamment. L'utilisateur peut donc recevoir une mise à jour malgré son choix dans les réglages.

Correction : définir une politique cohérente entre service et application, avec un réglage système adapté aux machines multi-utilisateurs, puis vérifier le comportement après fermeture de session et redémarrage.

### 4. Moyenne — transferts Windows interrompus : fichiers et handles conservés

**Correction locale préparée le 4 novembre 2026 :** récepteur extrait dans
`FileTransfer.cs`, réception dans un fichier temporaire jusqu'à la fin, nettoyage
sur déconnexion, réutilisation d'identifiant ou erreur d'écriture. Réception et
déconnexion sont synchronisées. Tests compilés et exécutés sous Windows : abandon,
reconnexion simulée, identifiant réutilisé, réception complète et collision.

Sources : `windows/src/Extras.cs:133–185`, `windows/src/App.cs:58,360–363`.

Le récepteur FileTransfer appartient à l'application et conserve ses transferts entre les sessions. Un FileStream n'est fermé que sur une trame de fin. Une déconnexion en milieu de transfert laisse le fichier partiel ouvert et son entrée en mémoire ; un nouveau début avec le même identifiant remplace l'entrée sans fermer l'ancien flux. Une succession de transferts interrompus peut accumuler handles et fichiers incomplets.

Correction : annuler et fermer les transferts à la déconnexion, traiter les identifiants réutilisés et enregistrer sous un nom temporaire jusqu'à réception complète. Tester coupure réseau puis reconnexion.

### 5. Moyenne — le déploiement iPad peut annoncer un succès après échec d'installation

**Correction locale préparée le 4 novembre 2026 :** arrêt immédiat sur erreur de
dpkg, contrôle de la version après installation, suppression de continue-on-error.
Le workflow peut seulement différer une connexion SSH indisponible au contrôle
préalable ; une erreur pendant l'installation bloque la publication. Tests Python
et shell exécutés sans contacter d'appareil ; recette sur iPad encore nécessaire.

Source : `ipad/deploy.py:26–32`.

La commande SSH enchaîne `dpkg -i ... && uicache ...;` puis plusieurs `if`. Le statut final provient de la dernière construction shell, ce qui peut masquer l'échec de dpkg. Le workflow tolère en plus l'échec de cette étape pour permettre une publication quand l'iPad est absent ; sa réussite globale ne prouve donc pas que l'appareil a été mis à jour.

Correction : interrompre immédiatement sur échec de dpkg, distinguer appareil injoignable et installation invalide, puis vérifier la version installée avant de déclarer le déploiement réussi.

## Livraison et vérifications

- Les trois derniers workflows consultés ont réussi, dont celui du commit audité : https://github.com/BenderRobot/bsidedesk/actions/runs/41807849149.
- Analyse syntaxique Python réussie : ipad/build.py, ipad/deploy.py, linux/bsidedesk.py et linux/bsidedesk-update. Aucun de ces scripts de déploiement n'a été exécuté.
- Aucune compilation locale Windows effectuée : pas de SDK .NET ni de compilateur Mono trouvé dans le PATH ; le projet est compilé avec Mono dans GitHub Actions.
- Pas de validation fonctionnelle sur iPad, GNOME, écran virtuel, USB, caméra ou audio. Les constats ci-dessus viennent du code ; ils ne constituent pas un test bout en bout.
- Le workflow principal publie directement après compilation ; il manque une étape de validation des changements avant livraison automatique. Ajouter un workflow de vérification des pull requests, sans accès aux appareils ni déploiement.
- Les mises à jour téléchargées ne font pas l'objet d'une vérification cryptographique explicite des artefacts dans les installateurs de mise à jour. La confiance repose sur HTTPS et le dépôt GitHub. Envisager un manifeste signé et sa vérification, surtout pour le paquet Debian exécuté avec les droits système.

## Ordre de reprise proposé

1. Corriger la machine à états d'authentification et ajouter ses tests de non-régression.
2. Sécuriser le remplacement Windows et la remontée d'erreur du déploiement iPad.
3. Nettoyer les transferts interrompus et unifier le réglage des mises à jour Debian.
4. Mettre en place la validation avant publication, puis une recette sur les trois appareils (appairage, veille, rotation, saisie, transferts, audio et reconnexion).

Pas de refonte générale nécessaire avant ces corrections ciblées. Préserver le protocole commun et documenter ses états avant de découper progressivement les deux fichiers principaux.
