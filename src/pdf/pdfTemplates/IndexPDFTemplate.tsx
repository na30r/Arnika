import { Document, Page, Text, Font, Image, View, StyleSheet } from '@react-pdf/renderer';
import { pdfjs } from 'react-pdf';
import Header from '../images/Header.png';
import Footer from '../images/Footer.png';
import persianFontwoff from '../../styles/fonts/Yekan.woff';

Font.register({
  family: 'PersianFont',
  src: persianFontwoff,
  format: 'truetype',
  fontWeight: 'normal',
  fontStyle: 'normal',
});

pdfjs.GlobalWorkerOptions.workerSrc = `//cdnjs.cloudflare.com/ajax/libs/pdf.js/${pdfjs.version}/pdf.worker.js`;

const IndexPDFTemplate = (img: string) => {
  const styles = StyleSheet.create({
    page: {
      flexDirection: 'column',
      backgroundColor: '#ffffff',
      padding: 20,
    },
    footer: {
      position: 'absolute',
      bottom: 0,
      left: 0,
      right: 0,
      marginLeft: 'auto',
      marginRight: 'auto',
    },
    main: {
      padding: 10,
      marginTop: 20,
    },
    title: {
      fontSize: 15,
      fontFamily: 'PersianFont',
    },
    container: {
      display: 'flex',
      flexDirection: 'row',
      justifyContent: 'flex-end',
      marginRight: 10,
      marginTop: 100,
    },
  });
  return (
    <Document>
      <Page>
        <Image src={Header} />
        <View style={styles.container}>
          <Text style={styles.title}>
            نمودار ارزیابی شرکت ها در بازه سه ماهه اول سال جاری به شرح زیر است :{' '}
          </Text>
        </View>
        <View style={styles.main}>
          <Image src={img} />
        </View>
        <View style={styles.footer}>
          <Image src={Footer} />
        </View>
      </Page>
    </Document>
  );
};

export default IndexPDFTemplate;
