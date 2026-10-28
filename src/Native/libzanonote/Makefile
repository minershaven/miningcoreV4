INC_DIRS = -I. -Icontrib -Icontrib/epee/include -Icontrib/ethereum/libethash
CFLAGS = -g -Wall -O3 -fPIC -Wno-comment -DNDEBUG -DUSE_OPEN_SSL_FOR_ECDSA -Ofast -funroll-loops -fvariable-expansion-in-unroller -ftree-loop-if-convert-stores -fmerge-all-constants -fbranch-target-load-optimize2 -g3 -std=gnu17 $(CPU_FLAGS) $(INC_DIRS) -march=native
CXXFLAGS = -g -Wall -O3 -fPIC -Wno-reorder -Wno-comment -DNDEBUG -DUSE_OPEN_SSL_FOR_ECDSA -Ofast -s -funroll-loops -fvariable-expansion-in-unroller -ftree-loop-if-convert-stores -fmerge-all-constants -fbranch-target-load-optimize2 -g3 -std=gnu++17 $(CPU_FLAGS) $(INC_DIRS) -march=native
LDFLAGS = -shared
LDLIBS = -lboost_date_time -lboost_filesystem -lboost_locale -lboost_serialization -lboost_system -lcrypto -lssl -lz
TARGET  = libzanonote.so

OBJECTS = exports.o \
	common/base58.o \
	crypto/crypto.o \
	crypto/crypto-ops.o \
	crypto/crypto-ops-data.o \
        crypto/crypto-sugar.o \
	crypto/hash.o \
	crypto/keccak.o \
        crypto/random.o \
        crypto/range_proofs.o \
	crypto/clsag.o \
	crypto/zarcanum.o \
	crypto/msm.o \
	crypto/one_out_of_many_proofs.o \
	crypto/tree-hash.o \
	crypto/chacha.o \
	crypto/blake2b-ref.o \
	common/mnemonic-encoding.o \
	currency_core/basic_pow_helpers.o \
	currency_core/currency_format_utils.o \
	currency_core/currency_format_utils_blocks.o \
	currency_core/currency_format_utils_transactions.o \
	currency_core/genesis.o \
	currency_core/genesis_acc.o \
	contrib/ethereum/libethash/ethash.o \
	contrib/ethereum/libethash/progpow.o \
	contrib/ethereum/libethash/primes.o \
	contrib/ethereum/libethash/keccak.o \
	contrib/ethereum/libethash/keccakf800.o \
	contrib/ethereum/libethash/keccakf1600.o \
	contrib/ethereum/libethash/managed.o

all: $(TARGET)

$(TARGET): $(OBJECTS)
	$(CXX) $(LDFLAGS) -o $@ $^ $(LDLIBS)

.PHONY: clean

clean:
	$(RM) $(TARGET) $(OBJECTS)
