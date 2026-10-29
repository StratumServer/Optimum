{
  description = "Optimum source bootstrap development environment";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" ];
      forAllSystems = nixpkgs.lib.genAttrs systems;
    in {
      devShells = forAllSystems (system:
        let
          pkgs = import nixpkgs { inherit system; };
        in {
          default = pkgs.mkShell {
            packages = [
              pkgs.dotnetCorePackages.sdk_10_0
              pkgs.git
              pkgs.perl
              pkgs.python3
              pkgs.curl
              pkgs.gnutar
              pkgs.coreutils
              pkgs.findutils
              pkgs.gnugrep
              pkgs.gnused
              pkgs.gawk
              pkgs.gnumake
              pkgs.cmake
              pkgs.unzip
              pkgs.powershell
            ];

            shellHook = ''
              echo "Optimum source bootstrap environment"
              echo "SDK selected by global.json: $(dotnet --version)"
            '';
          };
        });
    };
}
