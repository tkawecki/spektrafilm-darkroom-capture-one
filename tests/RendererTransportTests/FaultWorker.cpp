// Deliberately malformed peer for transport recovery tests; never a runtime renderer.
#include <cstdint>
#include <fcntl.h>
#include <io.h>
#include <iostream>
#include <sstream>
#include <string>
#include <fstream>
#include <thread>
#include <chrono>
#include <vector>
int main(int argc, char** argv) {
    _setmode(_fileno(stdin), _O_BINARY); _setmode(_fileno(stdout), _O_BINARY);
    std::string line;
    uint32_t width=0, height=0;
    while (std::getline(std::cin, line)) {
        uint32_t reply[2]{};
        if (line == "HELLO 2") { // A valid handshake, so the tests reach the malformed replies below.
            const std::string hello = "{\"protocol\":2}";
            reply[1] = static_cast<uint32_t>(hello.size());
            std::cout.write(reinterpret_cast<const char*>(reply), sizeof(reply)); std::cout << hello; std::cout.flush();
            continue;
        }
        if (line.rfind("MAP ",0)==0 || line.rfind("SOURCE ",0)==0) reply[1]=1; // Invalid success acknowledgement.
        else {
            std::istringstream command(line); std::string verb; command>>verb;
            if (verb.rfind("FRAME",0)==0) {
                command>>width>>height;
                std::cin.ignore(static_cast<std::streamsize>(width)*height*16);
                if ((argc == 3 || argc == 4) && std::string(argv[1]) == "--slow-frame") {
                    std::ofstream(argv[2]) << "received";
                    std::this_thread::sleep_for(std::chrono::seconds(argc == 4 ? std::stoi(argv[3]) : 5));
                }
            }
            reply[1]=width*height*(verb.back()=='8'?4:16);
        }
        std::cout.write(reinterpret_cast<const char*>(reply),sizeof(reply)); std::cout.flush();
        if ((argc == 3 || argc == 4) && std::string(argv[1]) == "--slow-frame") {
            std::vector<char> pixels(reply[1]);
            std::cout.write(pixels.data(), pixels.size()); std::cout.flush();
        }
    }
}
